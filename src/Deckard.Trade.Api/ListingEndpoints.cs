using System.Security.Claims;
using Deckard.Core.Trade;
using Microsoft.EntityFrameworkCore;

namespace Deckard.Trade.Api;

public static class ListingEndpoints
{
    public const int PageSize = 50;
    public const int MaxPage = 19;            // 1000 results per search at most: the market can't be downloaded in bulk
    public const int MaxActiveListings = 200; // per player
    public static readonly TimeSpan ListingLifetime = TimeSpan.FromDays(14);
    public static readonly TimeSpan SoldPricesShown = TimeSpan.FromDays(30); // price checks look this far back
    public const int MaxPricesChecked = 1000;
    public const int ProvenTrades = 5; // SellerTrust.Proven
    public const int MaxTwins = 3;     // identical live listings per seller

    public static void MapListingEndpoints(this WebApplication app)
    {
        var listings = app.MapGroup("/listings").RequireRateLimiting("player");

        listings.MapPost("/", async (NewListing request, ClaimsPrincipal player, TradeDb db, ListingRules rules) =>
        {
            var seller = await CurrentUser(player, db);
            if (seller is null || seller.Banned)
                return Results.Forbid();
            var (row, problem) = await NewRow(request, seller, db, rules);
            if (row is null)
                return problem!;
            db.Listings.Add(row);
            await db.SaveChangesAsync();
            return Results.Created($"/listings/{row.Id}", ToListing(row, revealSeller: true));
        }).RequireRateLimiting("listing");

        listings.MapPost("/search", async (ListingSearch search, ClaimsPrincipal player, TradeDb db, ListingRules rules) =>
        {
            var page = Math.Clamp(search.Page, 0, MaxPage);
            var now = DateTimeOffset.UtcNow;
            var hidden = BlockEndpoints.Hidden(db, PlayerId(player));
            var query = Matching(Live(db.Listings.Include(l => l.Seller).Include(l => l.Affixes).AsSplitQuery(), now, db), search, rules, db)
                .Where(l => !hidden.Contains(l.SellerId));
            if (search.SellerInGame)
            {
                var since = now - PresenceEndpoints.OnlineWindow;
                query = query.Where(l => l.Seller!.ShowOnline && l.Seller.InGame && l.Seller.LastSeen > since);
            }
            // Best-offer listings (no price) come last on either price order.
            query = search.Sort switch
            {
                ListingSort.PriceHigh => query.OrderBy(l => l.PriceGold == null).ThenByDescending(l => l.PriceGold).ThenByDescending(l => l.Created),
                ListingSort.Newest => query.OrderByDescending(l => l.Created),
                ListingSort.ItemPower => query.OrderByDescending(l => l.ItemPower).ThenBy(l => l.PriceGold == null).ThenBy(l => l.PriceGold),
                _ => query.OrderBy(l => l.PriceGold == null).ThenBy(l => l.PriceGold).ThenByDescending(l => l.Created),
            };
            var rows = await query.Skip(page * PageSize).Take(PageSize + 1).ToListAsync();
            return Results.Ok(new SearchPage([.. rows.Take(PageSize).Select(r => ToListing(r))], page, rows.Count > PageSize && page < MaxPage));
        });

        // What items like this go for: asking prices of live listings, and what similar ones sold for lately.
        listings.MapPost("/price-check", async (ListingSearch search, TradeDb db, ListingRules rules) =>
        {
            var now = DateTimeOffset.UtcNow;
            var asking = await Matching(Live(db.Listings, now, db), search, rules, db)
                .Where(l => l.PriceGold != null).Select(l => l.PriceGold!.Value).OrderBy(p => p).Take(MaxPricesChecked).ToListAsync();
            // Sales count only at the price of the offer both players accepted: a seller's own figure on /sold could set
            // anyone's price check.
            var since = now - SoldPricesShown;
            var soldListings = Matching(db.Listings.Where(l => l.Status == ListingStatus.Sold && l.SoldAt > since), search, rules, db);
            var sold = await db.Offers.Where(o => o.Status == OfferStatus.Accepted && soldListings.Any(l => l.Id == o.ListingId))
                .Select(o => o.PriceGold).OrderBy(p => p).Take(MaxPricesChecked).ToListAsync();
            return Results.Ok(new PriceCheck(asking.Count, asking.Count > 0 ? asking[0] : null, Median(asking),
                asking.Count > 0 ? asking[^1] : null, sold.Count, Median(sold)));
        });

        // What items like this were listed and sold for, day by day over the price checks' window: the asking prices of
        // listings made that day (whatever became of them), and the accepted offers of those sold that day.
        listings.MapPost("/price-history", async (ListingSearch search, TradeDb db, ListingRules rules) =>
        {
            var since = DateTimeOffset.UtcNow - SoldPricesShown;
            var listed = await Matching(db.Listings.Where(l => l.Created > since && l.PriceGold != null && !Moderation.OffMarket(db).Contains(l.SellerId)), search, rules, db)
                .Select(l => new { l.Created, Price = l.PriceGold!.Value }).Take(MaxPricesChecked * 5).ToListAsync();
            var soldListings = Matching(db.Listings.Where(l => l.Status == ListingStatus.Sold && l.SoldAt > since), search, rules, db);
            var sold = await db.Offers.Where(o => o.Status == OfferStatus.Accepted && soldListings.Any(l => l.Id == o.ListingId))
                .Select(o => new { o.Listing!.SoldAt, Price = o.PriceGold }).Take(MaxPricesChecked * 5).ToListAsync();
            var days = listed.Select(l => DateOnly.FromDateTime(l.Created.UtcDateTime))
                .Concat(sold.Select(s => DateOnly.FromDateTime(s.SoldAt!.Value.UtcDateTime))).Distinct().Order();
            return Results.Ok(new PriceHistory([.. days.Select(day =>
            {
                List<long> asking = [.. listed.Where(l => DateOnly.FromDateTime(l.Created.UtcDateTime) == day).Select(l => l.Price).Order()];
                List<long> paid = [.. sold.Where(s => DateOnly.FromDateTime(s.SoldAt!.Value.UtcDateTime) == day).Select(s => s.Price).Order()];
                return new PricePoint(day, asking.Count, Median(asking), paid.Count, Median(paid));
            })]));
        });

        listings.MapGet("/mine", async (ClaimsPrincipal player, TradeDb db) =>
        {
            var id = PlayerId(player);
            await OfferEndpoints.DeclineExpired(db, id, DateTimeOffset.UtcNow); // before the offers on each are counted
            var rows = await db.Listings.Include(l => l.Seller).Include(l => l.Affixes).AsSplitQuery()
                .Where(l => l.SellerId == id && (l.Status == ListingStatus.Active || l.Status == ListingStatus.Reserved))
                .OrderByDescending(l => l.Created).ToListAsync();
            return Results.Ok(rows.Select(r => ToListing(r, revealSeller: true)).ToList());
        });

        // Only the seller can close their listing: sold in game, or taken down.
        listings.MapPost("/{id:guid}/sold", (Guid id, SoldRequest? request, ClaimsPrincipal player, TradeDb db) =>
            request?.PriceGold is { } price && (price <= 0 || price > ListingRules.MaxPriceGold)
                ? Task.FromResult(Results.ValidationProblem(new Dictionary<string, string[]> { ["sold"] = ["The sold price must be between 1 gold and the most a listing can ask."] }))
                : Close(id, ListingStatus.Sold, player, db, request?.PriceGold));
        listings.MapDelete("/{id:guid}", (Guid id, ClaimsPrincipal player, TradeDb db) => Close(id, ListingStatus.Withdrawn, player, db));

        // Another 14 days for a listing still up (or just expired), without listing the item again.
        listings.MapPost("/{id:guid}/renew", async (Guid id, ClaimsPrincipal player, TradeDb db) =>
        {
            var row = await db.Listings.Include(l => l.Seller).Include(l => l.Affixes).AsSplitQuery()
                .SingleOrDefaultAsync(l => l.Id == id && l.SellerId == PlayerId(player) && (l.Status == ListingStatus.Active || l.Status == ListingStatus.Reserved));
            if (row is null)
                return Results.NotFound();
            row.Expires = DateTimeOffset.UtcNow + ListingLifetime;
            await db.SaveChangesAsync();
            return Results.Ok(ToListing(row, revealSeller: true));
        });
    }

    /// <summary>A new listing of the seller's, checked against the season's rules and the seller's listing limit; or why not.</summary>
    internal static async Task<(ListingRow? Row, IResult? Problem)> NewRow(NewListing request, UserRow seller, TradeDb db, ListingRules rules)
    {
        if (rules.Check(request) is { Count: > 0 } errors)
            return (null, Results.ValidationProblem(new Dictionary<string, string[]> { ["listing"] = [.. errors] }));
        var now = DateTimeOffset.UtcNow;
        var mine = db.Listings.Where(l => l.SellerId == seller.Id && (l.Status == ListingStatus.Active || l.Status == ListingStatus.Reserved) && l.Expires > now);
        if (await mine.CountAsync() >= MaxActiveListings)
            return (null, Results.Problem($"You can have at most {MaxActiveListings} active listings.", statusCode: StatusCodes.Status409Conflict));
        // The same item over and over would only crowd the market. A few are fine: fixed-roll items like runewords can be
        // truly identical, and goods are counted, so another stack is always fine.
        if (!rules.IsGoods(request.ItemType))
        {
            var (realm, name) = (request.Realm, request.Name.Trim());
            var twins = await mine.Include(l => l.Affixes)
                .Where(l => l.Realm == realm.Kind && l.Hardcore == realm.Hardcore && l.Season == realm.Season && l.ItemType == request.ItemType
                            && l.Name == name && l.ItemPower == request.ItemPower && l.AspectId == request.AspectId)
                .ToListAsync();
            if (twins.Count(t => t.Affixes.OrderBy(a => a.Index).Select(a => (a.AffixId, a.Value)).SequenceEqual(request.Affixes.Select(a => (a.Id, a.Value)))) >= MaxTwins)
                return (null, Results.Problem($"You already list this item {MaxTwins} times.", statusCode: StatusCodes.Status409Conflict));
        }

        var row = new ListingRow
        {
            Id = Guid.NewGuid(),
            SellerId = seller.Id,
            Realm = request.Realm.Kind,
            Hardcore = request.Realm.Hardcore,
            Season = request.Realm.Season,
            ItemType = request.ItemType,
            Name = request.Name.Trim(),
            Rarity = request.Rarity,
            ItemPower = request.ItemPower,
            Ancestral = request.Ancestral,
            AspectId = request.AspectId,
            ClassMask = rules.ClassMask(request),
            PriceGold = request.PriceGold,
            Quantity = request.Quantity,
            Status = ListingStatus.Active,
            Created = now,
            Expires = now + ListingLifetime,
            Affixes = [.. request.Affixes.Select((a, i) => new ListingAffixRow { Index = i, AffixId = a.Id, Value = a.Value, Greater = a.Greater, Slot = a.Slot })],
        };
        row.Seller = seller;
        return (row, null);
    }

    /// <summary>Listings open to offers: active, not expired, from a seller who isn't banned or waiting for review.</summary>
    internal static IQueryable<ListingRow> Live(IQueryable<ListingRow> listings, DateTimeOffset now, TradeDb db)
    {
        var offMarket = Moderation.OffMarket(db);
        return listings.Where(l => l.Status == ListingStatus.Active && l.Expires > now && !offMarket.Contains(l.SellerId));
    }

    /// <summary>The search's filters: realm, item, price, class, aspect, affix rolls and the sellers' records; shared by search and price checks.</summary>
    private static IQueryable<ListingRow> Matching(IQueryable<ListingRow> query, ListingSearch search, ListingRules rules, TradeDb db)
    {
        query = query.Where(l => l.Realm == search.Realm.Kind && l.Hardcore == search.Realm.Hardcore && l.Season == search.Realm.Season);
        if (search.ItemType is { } type)
            query = query.Where(l => l.ItemType == type);
        if (search.Rarity is { } rarity)
            query = query.Where(l => l.Rarity == rarity);
        if (search.Name is { } name)
            query = query.Where(l => l.Name == name);
        if (search.MinItemPower is { } power)
            query = query.Where(l => l.ItemPower >= power);
        if (search.MinPriceGold is { } minPrice)
            query = query.Where(l => l.PriceGold != null && l.PriceGold >= minPrice);
        if (search.MaxPriceGold is { } maxPrice)
            query = query.Where(l => l.PriceGold != null && l.PriceGold <= maxPrice);
        if (search.Class is { } className)
        {
            var bit = rules.ClassBit(className);
            query = query.Where(l => (l.ClassMask & bit) != 0);
        }
        if (search.AspectId is { } aspect)
            query = query.Where(l => l.AspectId == aspect);
        foreach (var filter in (search.Affixes ?? []).Take(ListingRules.MaxAffixLines))
        {
            var (id, min, max, greater) = (filter.Id, filter.Min ?? double.MinValue, filter.Max ?? double.MaxValue, filter.Greater);
            query = query.Where(l => l.Affixes.Any(a => a.AffixId == id && a.Value >= min && a.Value <= max && (a.Greater || !greater)));
        }
        if (search.MinGreaterAffixes > 0)
            query = query.Where(l => l.Affixes.Count(a => a.Greater) >= search.MinGreaterAffixes);
        // A seller's record as OfferEndpoints.Records counts it: deals the other side completed or reported.
        if (search.Sellers == SellerTrust.NoReports)
            query = query.Where(l => !db.Offers.Any(o => (o.BuyerId == l.SellerId && o.SellerReport != null) || (o.Listing!.SellerId == l.SellerId && o.BuyerReport != null)));
        else if (search.Sellers == SellerTrust.Proven)
            query = query.Where(l =>
                db.Offers.Count(o => (o.BuyerId == l.SellerId && o.SellerDone) || (o.Listing!.SellerId == l.SellerId && o.BuyerDone)) >= ProvenTrades
                && db.Offers.Count(o => (o.BuyerId == l.SellerId && o.SellerReport != null) || (o.Listing!.SellerId == l.SellerId && o.BuyerReport != null)) * 10
                   <= db.Offers.Count(o => (o.BuyerId == l.SellerId && o.SellerDone) || (o.Listing!.SellerId == l.SellerId && o.BuyerDone)));
        return query;
    }

    private static long? Median(List<long> sorted) => sorted.Count == 0 ? null : sorted[sorted.Count / 2];

    /// <param name="soldPrice">For a sale: what it went for; else the accepted offer's price, else the asking price.</param>
    private static async Task<IResult> Close(Guid id, ListingStatus status, ClaimsPrincipal player, TradeDb db, long? soldPrice = null)
    {
        var row = await db.Listings.SingleOrDefaultAsync(l => l.Id == id && l.SellerId == PlayerId(player));
        if (row is null)
            return Results.NotFound();
        row.Status = status;
        if (status == ListingStatus.Sold)
        {
            row.SoldAt = DateTimeOffset.UtcNow;
            row.SoldPriceGold = soldPrice
                ?? await db.Offers.Where(o => o.ListingId == id && o.Status == OfferStatus.Accepted).Select(o => (long?)o.PriceGold).FirstOrDefaultAsync()
                ?? row.PriceGold;
        }
        await db.SaveChangesAsync();
        // Pending offers lapse with the listing; a withdrawn listing also calls off an accepted deal.
        var now = DateTimeOffset.UtcNow;
        await db.Offers.Where(o => o.ListingId == id && (o.Status == OfferStatus.Pending || o.Status == OfferStatus.Countered))
            .ExecuteUpdateAsync(o => o.SetProperty(x => x.Status, OfferStatus.Declined).SetProperty(x => x.Updated, now));
        if (status == ListingStatus.Withdrawn)
            await db.Offers.Where(o => o.ListingId == id && o.Status == OfferStatus.Accepted)
                .ExecuteUpdateAsync(o => o.SetProperty(x => x.Status, OfferStatus.Cancelled).SetProperty(x => x.Updated, now));
        return Results.NoContent();
    }

    internal static Guid PlayerId(ClaimsPrincipal player) =>
        Guid.TryParse(player.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    internal static Task<UserRow?> CurrentUser(ClaimsPrincipal player, TradeDb db)
    {
        var id = PlayerId(player);
        return db.Users.SingleOrDefaultAsync(u => u.Id == id);
    }

    /// <param name="revealSeller">Only for the seller's own listings: others learn the BattleTag from an accepted offer.</param>
    internal static Listing ToListing(ListingRow row, bool revealSeller = false) => new(
        row.Id, revealSeller ? row.Seller?.BattleTag ?? "" : "", new Realm(row.Realm, row.Hardcore, row.Season), row.ItemType, row.Name, row.Rarity,
        row.ItemPower, row.Ancestral, row.AspectId,
        [.. row.Affixes.OrderBy(a => a.Index).Select(a => new ListedAffix(a.AffixId, a.Value, a.Greater, a.Slot))],
        row.PriceGold, row.Created, row.Expires, row.Status == ListingStatus.Reserved, PresenceEndpoints.IsInGame(row.Seller), row.Quantity);
}
