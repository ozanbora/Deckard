using System.Security.Claims;
using System.Text.Json;
using Deckard.Core.Trade;
using Microsoft.EntityFrameworkCore;

namespace Deckard.Trade.Api;

/// <summary>
/// Buy orders: a player posts what they want (a search) and the gold they pay for one. Sellers browse them, or Deckard
/// finds the ones an item read in game fits; selling to one lists the item and makes the buyer's accepted offer at the
/// order's price in one step, so the deal shows in both players' offers and shares their BattleTags as any accepted
/// offer does. The buyer's BattleTag is never shown on the order itself.
/// </summary>
public static class WantedEndpoints
{
    public const int MaxOpen = 50; // per player
    public const int MaxMatched = 5;
    public const int MaxScanned = 2000; // open orders of a realm and type looked at per match
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    public static void MapWantedEndpoints(this WebApplication app)
    {
        var wanted = app.MapGroup("/wanted").RequireRateLimiting("player");

        wanted.MapPost("/", async (NewWanted request, ClaimsPrincipal player, TradeDb db, ListingRules rules) =>
        {
            if (Check(request, rules) is { Count: > 0 } errors)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["wanted"] = [.. errors] });
            var buyer = await ListingEndpoints.CurrentUser(player, db);
            if (buyer is null || buyer.Banned)
                return Results.Forbid();
            var now = DateTimeOffset.UtcNow;
            if (await db.Wanted.CountAsync(w => w.BuyerId == buyer.Id && w.Status == WantedStatus.Open && w.Expires > now) >= MaxOpen)
                return Results.Problem($"You can have at most {MaxOpen} open buy orders.", statusCode: StatusCodes.Status409Conflict);
            var want = request.Want with { Page = 0, Sort = ListingSort.PriceLow, SellerInGame = false, MinPriceGold = null, MaxPriceGold = null };
            var row = new WantedRow
            {
                Id = Guid.NewGuid(), BuyerId = buyer.Id, Realm = want.Realm.Kind, Hardcore = want.Realm.Hardcore, Season = want.Realm.Season,
                ItemType = want.ItemType, Want = JsonSerializer.Serialize(want), PriceGold = request.PriceGold, Status = WantedStatus.Open,
                Created = now, Expires = now + Lifetime,
            };
            db.Wanted.Add(row);
            await db.SaveChangesAsync();
            return Results.Created($"/wanted/{row.Id}", ToWanted(row, buyer.Id));
        }).RequireRateLimiting("listing");

        // The player's own orders: open ones, and those filled or cancelled lately.
        wanted.MapGet("/mine", async (ClaimsPrincipal player, TradeDb db) =>
        {
            var me = ListingEndpoints.PlayerId(player);
            var since = DateTimeOffset.UtcNow - OfferEndpoints.HistoryShown;
            var rows = await db.Wanted.Where(w => w.BuyerId == me && (w.Status == WantedStatus.Open || w.Created > since))
                .OrderByDescending(w => w.Created).Take(MaxOpen * 2).ToListAsync();
            return Results.Ok(rows.Select(w => ToWanted(w, me)).ToList());
        });

        wanted.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal player, TradeDb db) =>
        {
            var me = ListingEndpoints.PlayerId(player);
            var cancelled = await db.Wanted.Where(w => w.Id == id && w.BuyerId == me && w.Status == WantedStatus.Open)
                .ExecuteUpdateAsync(w => w.SetProperty(x => x.Status, WantedStatus.Cancelled));
            return cancelled == 0 ? Results.NotFound() : Results.NoContent();
        });

        // Others' open orders in a realm (and item type), best paying first.
        wanted.MapPost("/browse", async (ListingSearch search, ClaimsPrincipal player, TradeDb db) =>
        {
            var me = ListingEndpoints.PlayerId(player);
            var page = Math.Clamp(search.Page, 0, ListingEndpoints.MaxPage);
            var hidden = BlockEndpoints.Hidden(db, me);
            var query = Open(db, search.Realm, DateTimeOffset.UtcNow).Where(w => w.BuyerId != me && !hidden.Contains(w.BuyerId));
            if (search.ItemType is { } type)
                query = query.Where(w => w.ItemType == type);
            var rows = await query.OrderByDescending(w => w.PriceGold).ThenByDescending(w => w.Created)
                .Skip(page * ListingEndpoints.PageSize).Take(ListingEndpoints.PageSize + 1).ToListAsync();
            return Results.Ok(new WantedPage([.. rows.Take(ListingEndpoints.PageSize).Select(w => ToWanted(w, me))], page,
                rows.Count > ListingEndpoints.PageSize && page < ListingEndpoints.MaxPage));
        });

        // The orders an item fits (the item Deckard read in game), best paying first.
        wanted.MapPost("/match", async (NewListing item, ClaimsPrincipal player, TradeDb db, ListingRules rules) =>
        {
            var me = ListingEndpoints.PlayerId(player);
            var rows = await Candidates(db, item, me).Take(MaxScanned).ToListAsync();
            return Results.Ok(rows.Where(w => Fits(Search(w), item, rules)).Take(MaxMatched).Select(w => ToWanted(w, me)).ToList());
        });

        // Sell the item to the order: list it at the order's price and accept the buyer's offer for it, in one step.
        wanted.MapPost("/{id:guid}/fill", async (Guid id, NewListing item, ClaimsPrincipal player, TradeDb db, ListingRules rules) =>
        {
            var seller = await ListingEndpoints.CurrentUser(player, db);
            if (seller is null || await Moderation.AnyOffMarketAsync(db, seller.Id))
                return Results.Forbid();
            var now = DateTimeOffset.UtcNow;
            var offMarket = Moderation.OffMarket(db);
            var order = await db.Wanted.Include(w => w.Buyer)
                .SingleOrDefaultAsync(w => w.Id == id && w.Status == WantedStatus.Open && w.Expires > now && !offMarket.Contains(w.BuyerId));
            if (order is null || await BlockEndpoints.Hidden(db, seller.Id).ContainsAsync(order.BuyerId))
                return Results.NotFound();
            if (order.BuyerId == seller.Id)
                return Problem("That's your own buy order.");
            if (!Fits(Search(order), item, rules))
                return Problem("This item doesn't fit the buy order.");
            var (listing, problem) = await ListingEndpoints.NewRow(item with { PriceGold = order.PriceGold, Quantity = 1 }, seller, db, rules);
            if (listing is null)
                return problem!;
            // Take the order atomically: of two sellers at once, only one fills it; the listing and deal go with it or not at all.
            await using var transaction = await db.Database.BeginTransactionAsync();
            if (await db.Wanted.Where(w => w.Id == id && w.Status == WantedStatus.Open).ExecuteUpdateAsync(w => w.SetProperty(x => x.Status, WantedStatus.Filled)) == 0)
                return Results.Problem("Someone else just sold to this buy order.", statusCode: StatusCodes.Status409Conflict);
            listing.Status = ListingStatus.Reserved;
            var offer = new OfferRow
            {
                Id = Guid.NewGuid(), ListingId = listing.Id, Listing = listing, BuyerId = order.BuyerId, Buyer = order.Buyer,
                PriceGold = order.PriceGold, Status = OfferStatus.Accepted, Created = now, Updated = now,
            };
            db.Listings.Add(listing);
            db.Offers.Add(offer);
            order.Status = WantedStatus.Filled;
            order.OfferId = offer.Id;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.Ok(await OfferEndpoints.ToOffer(db, offer, seller.Id));
        }).RequireRateLimiting("listing");
    }

    /// <summary>What's wrong with a buy order: an unknown realm, no price, or a search that would take any item at all.</summary>
    internal static List<string> Check(NewWanted request, ListingRules rules)
    {
        var errors = new List<string>();
        rules.CheckRealm(request.Want.Realm, errors);
        if (request.PriceGold is < 1 or > ListingRules.MaxPriceGold)
            errors.Add($"The price must be between 1 and {ListingRules.MaxPriceGold:N0} gold.");
        if (request.Want.Name is { Length: > 100 })
            errors.Add("The item name must be at most 100 characters.");
        if (request.Want.ItemType is null && request.Want.AspectId is null && request.Want.Name is null)
            errors.Add("Say what you want: an item type, a power or a name.");
        if ((request.Want.Affixes?.Count ?? 0) > ListingRules.MaxAffixLines)
            errors.Add($"A buy order asks for at most {ListingRules.MaxAffixLines} affixes.");
        return errors;
    }

    /// <summary>
    /// Whether an item fits a buy order: the search filters of /listings/search (realm, type, rarity, name, power, class,
    /// aspect, affix rolls, greater affixes), applied to one item. Price, order and seller filters don't apply.
    /// </summary>
    internal static bool Fits(ListingSearch want, NewListing item, ListingRules rules) =>
        want.Realm == item.Realm
        && (want.ItemType is null || want.ItemType == item.ItemType)
        && (want.Rarity is null || want.Rarity == item.Rarity)
        && (want.Name is null || want.Name == item.Name.Trim())
        && (want.MinItemPower is null || item.ItemPower >= want.MinItemPower)
        && (want.AspectId is null || want.AspectId == item.AspectId)
        && (want.Class is null || (rules.ClassMask(item) & rules.ClassBit(want.Class)) != 0)
        && (want.Affixes ?? []).Take(ListingRules.MaxAffixLines).All(f => item.Affixes.Any(a =>
            a.Id == f.Id && a.Value >= (f.Min ?? double.MinValue) && a.Value <= (f.Max ?? double.MaxValue) && (a.Greater || !f.Greater)))
        && item.Affixes.Count(a => a.Greater) >= want.MinGreaterAffixes;

    private static IQueryable<WantedRow> Open(TradeDb db, Realm realm, DateTimeOffset now)
    {
        var offMarket = Moderation.OffMarket(db);
        return db.Wanted.Where(w => w.Realm == realm.Kind && w.Hardcore == realm.Hardcore && w.Season == realm.Season
                                    && w.Status == WantedStatus.Open && w.Expires > now && !offMarket.Contains(w.BuyerId));
    }

    /// <summary>Others' open orders that could take the item (not blocked either way): its realm, its type or any type, best paying first.</summary>
    private static IQueryable<WantedRow> Candidates(TradeDb db, NewListing item, Guid me)
    {
        var hidden = BlockEndpoints.Hidden(db, me);
        return Open(db, item.Realm, DateTimeOffset.UtcNow)
            .Where(w => w.BuyerId != me && !hidden.Contains(w.BuyerId) && (w.ItemType == null || w.ItemType == item.ItemType))
            .OrderByDescending(w => w.PriceGold).ThenBy(w => w.Created);
    }

    private static ListingSearch Search(WantedRow row) => JsonSerializer.Deserialize<ListingSearch>(row.Want)!;

    private static Wanted ToWanted(WantedRow row, Guid me) =>
        new(row.Id, Search(row), row.PriceGold, row.Status, row.Created, row.Expires, row.BuyerId == me);

    private static IResult Problem(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["wanted"] = [message] });
}
