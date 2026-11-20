using System.Security.Claims;
using Deckard.Core.Trade;
using Microsoft.EntityFrameworkCore;

namespace Deckard.Trade.Api;

/// <summary>
/// Offers: a buyer offers the asking price or less, the seller accepts, declines or counters with a higher price, which
/// the buyer then accepts or answers with a revised offer. Accepting reserves the listing and is the only way a
/// BattleTag reaches another player: both sides then see each other's, to trade by hand in game. After the deal each
/// side marks it completed, or reports it; those make up the players' trading records.
/// </summary>
public static class OfferEndpoints
{
    public const int MaxOffersShown = 200;
    public const int MaxReportLength = 500;
    public static readonly TimeSpan HistoryShown = TimeSpan.FromDays(14);

    public static void MapOfferEndpoints(this WebApplication app)
    {
        app.MapPost("/listings/{id:guid}/offers", async (Guid id, NewOffer request, ClaimsPrincipal player, TradeDb db) =>
        {
            var buyer = await ListingEndpoints.CurrentUser(player, db);
            if (buyer is null || await Moderation.AnyOffMarketAsync(db, buyer.Id))
                return Results.Forbid();
            var now = DateTimeOffset.UtcNow;
            var listing = await ListingEndpoints.Live(db.Listings.Include(l => l.Seller).Include(l => l.Affixes).AsSplitQuery(), now, db)
                .SingleOrDefaultAsync(l => l.Id == id);
            if (listing is null || await BlockEndpoints.Hidden(db, buyer.Id).ContainsAsync(listing.SellerId))
                return Results.NotFound();
            if (listing.SellerId == buyer.Id)
                return Problem("You can't make an offer on your own listing.");
            if (!FitsAsking(request.PriceGold, listing))
                return Problem("Offer between 1 gold and the asking price.");
            if (await db.Offers.AnyAsync(o => o.ListingId == id && o.BuyerId == buyer.Id && (o.Status == OfferStatus.Pending || o.Status == OfferStatus.Countered)))
                return Results.Problem("You already have an offer on this listing.", statusCode: StatusCodes.Status409Conflict);

            var row = new OfferRow
            {
                Id = Guid.NewGuid(), ListingId = id, Listing = listing, BuyerId = buyer.Id, Buyer = buyer,
                PriceGold = request.PriceGold, Status = OfferStatus.Pending, Created = now, Updated = now,
            };
            db.Offers.Add(row);
            await db.SaveChangesAsync();
            return Results.Created($"/offers/{row.Id}", await ToOffer(db, row, buyer.Id));
        }).RequireRateLimiting("listing");

        var offers = app.MapGroup("/offers").RequireRateLimiting("player");

        // Polled by the app: offers on the player's live listings, and the player's own recent offers.
        offers.MapGet("/", async (ClaimsPrincipal player, TradeDb db) =>
        {
            var me = ListingEndpoints.PlayerId(player);
            await DeclineExpired(db, me, DateTimeOffset.UtcNow);
            var since = DateTimeOffset.UtcNow - HistoryShown;
            var rows = await Offers(db)
                .Where(o => (o.Listing!.SellerId == me
                             && (o.Status == OfferStatus.Pending || o.Status == OfferStatus.Countered || o.Status == OfferStatus.Accepted)
                             && (o.Listing.Status == ListingStatus.Active || o.Listing.Status == ListingStatus.Reserved))
                            || (o.BuyerId == me && o.Updated > since))
                .OrderByDescending(o => o.Updated).Take(MaxOffersShown).ToListAsync();
            var records = await Records(db, rows.Select(o => Other(o, me)).Distinct().ToList());
            return Results.Ok(new OfferBox(
                [.. rows.Where(o => o.Listing!.SellerId == me).Select(o => ToOffer(o, me, records))],
                [.. rows.Where(o => o.BuyerId == me).Select(o => ToOffer(o, me, records))]));
        });

        // The seller accepts an offer, or the buyer accepts the seller's counter-offer: either reserves the listing.
        offers.MapPost("/{id:guid}/accept", async (Guid id, ClaimsPrincipal player, TradeDb db) =>
        {
            var me = ListingEndpoints.PlayerId(player);
            var offer = await Offers(db).SingleOrDefaultAsync(o => o.Id == id && (o.Listing!.SellerId == me || o.BuyerId == me));
            if (offer is null)
                return Results.NotFound();
            var sellerAccepts = offer.Listing!.SellerId == me && offer.Status == OfferStatus.Pending;
            var buyerAccepts = offer.BuyerId == me && offer.Status == OfferStatus.Countered && offer.CounterGold is not null;
            if (offer.BuyerId == me && offer.Status != OfferStatus.Countered)
                return Results.NotFound(); // a buyer only ever accepts a counter-offer, never their own offer
            if (!sellerAccepts && !buyerAccepts || await Moderation.AnyOffMarketAsync(db, offer.BuyerId, offer.Listing.SellerId))
                return Results.Problem("This offer is no longer open.", statusCode: StatusCodes.Status409Conflict);
            // Reserve atomically: of two accepts at once on the same listing, only one wins.
            var now = DateTimeOffset.UtcNow;
            var reserved = await db.Listings.Where(l => l.Id == offer.ListingId && l.Status == ListingStatus.Active && l.Expires > now)
                .ExecuteUpdateAsync(l => l.SetProperty(x => x.Status, ListingStatus.Reserved));
            if (reserved == 0)
                return Results.Problem("This listing is no longer open.", statusCode: StatusCodes.Status409Conflict);
            if (buyerAccepts)
                (offer.PriceGold, offer.CounterGold) = (offer.CounterGold!.Value, null);
            offer.Status = OfferStatus.Accepted;
            offer.Updated = now;
            offer.Listing.Status = ListingStatus.Reserved;
            if (await SaveOrConflict(db) is { } conflict)
            {
                // The offer changed meanwhile (withdrawn, revised): give the listing back to the market.
                await db.Listings.Where(l => l.Id == offer.ListingId && l.Status == ListingStatus.Reserved)
                    .ExecuteUpdateAsync(l => l.SetProperty(x => x.Status, ListingStatus.Active));
                return conflict;
            }
            return Results.Ok(await ToOffer(db, offer, me));
        });

        // The seller answers with a higher price, up to the asking price (any amount on a best-offer listing).
        offers.MapPost("/{id:guid}/counter", async (Guid id, NewOffer counter, ClaimsPrincipal player, TradeDb db) =>
        {
            var me = ListingEndpoints.PlayerId(player);
            var offer = await Offers(db).SingleOrDefaultAsync(o => o.Id == id && o.Listing!.SellerId == me);
            if (offer is null)
                return Results.NotFound();
            if (offer.Status != OfferStatus.Pending)
                return Results.Problem("This offer is no longer open.", statusCode: StatusCodes.Status409Conflict);
            if (counter.PriceGold <= offer.PriceGold || !FitsAsking(counter.PriceGold, offer.Listing!))
                return Problem("Counter with more than the offer and no more than your asking price.");
            (offer.Status, offer.CounterGold, offer.Updated) = (OfferStatus.Countered, counter.PriceGold, DateTimeOffset.UtcNow);
            if (await SaveOrConflict(db) is { } conflict)
                return conflict;
            return Results.Ok(await ToOffer(db, offer, me));
        });

        // The buyer changes their offer (also the answer to a counter-offer); it waits for the seller again.
        offers.MapPost("/{id:guid}/revise", async (Guid id, NewOffer revised, ClaimsPrincipal player, TradeDb db) =>
        {
            var me = ListingEndpoints.PlayerId(player);
            var offer = await Offers(db).SingleOrDefaultAsync(o => o.Id == id && o.BuyerId == me);
            if (offer is null)
                return Results.NotFound();
            if (offer.Status is not (OfferStatus.Pending or OfferStatus.Countered))
                return Results.Problem("This offer is no longer open.", statusCode: StatusCodes.Status409Conflict);
            if (!FitsAsking(revised.PriceGold, offer.Listing!))
                return Problem("Offer between 1 gold and the asking price.");
            (offer.Status, offer.PriceGold, offer.CounterGold, offer.Updated) = (OfferStatus.Pending, revised.PriceGold, null, DateTimeOffset.UtcNow);
            if (await SaveOrConflict(db) is { } conflict)
                return conflict;
            return Results.Ok(await ToOffer(db, offer, me));
        });

        offers.MapPost("/{id:guid}/decline", (Guid id, ClaimsPrincipal player, TradeDb db) =>
            Move(db, ListingEndpoints.PlayerId(player), id, seller: true, OfferStatus.Declined));

        offers.MapDelete("/{id:guid}", (Guid id, ClaimsPrincipal player, TradeDb db) =>
            Move(db, ListingEndpoints.PlayerId(player), id, seller: false, OfferStatus.Withdrawn));

        // Either side calls off an accepted deal; the listing goes back on the market.
        offers.MapPost("/{id:guid}/cancel", async (Guid id, ClaimsPrincipal player, TradeDb db) =>
        {
            var me = ListingEndpoints.PlayerId(player);
            var offer = await Offers(db).SingleOrDefaultAsync(o => o.Id == id && (o.BuyerId == me || o.Listing!.SellerId == me));
            if (offer is null)
                return Results.NotFound();
            if (offer.Status != OfferStatus.Accepted || offer.Listing!.Status != ListingStatus.Reserved)
                return Results.Problem("There's no open deal to cancel.", statusCode: StatusCodes.Status409Conflict);
            offer.Status = OfferStatus.Cancelled;
            offer.Updated = DateTimeOffset.UtcNow;
            offer.Listing.Status = ListingStatus.Active;
            if (await SaveOrConflict(db) is { } conflict)
                return conflict;
            return Results.Ok(await ToOffer(db, offer, me));
        });

        // After trading in game: each side marks the deal completed; it counts on the other's record.
        offers.MapPost("/{id:guid}/complete", async (Guid id, ClaimsPrincipal player, TradeDb db) =>
        {
            var me = ListingEndpoints.PlayerId(player);
            var offer = await Offers(db).SingleOrDefaultAsync(o => o.Id == id && (o.BuyerId == me || o.Listing!.SellerId == me));
            if (offer is null)
                return Results.NotFound();
            if (offer.Status != OfferStatus.Accepted)
                return Results.Problem("Only an accepted deal can be completed.", statusCode: StatusCodes.Status409Conflict);
            if (offer.BuyerId == me)
                offer.BuyerDone = true;
            else
                offer.SellerDone = true;
            if (await SaveOrConflict(db) is { } conflict)
                return conflict;
            return Results.Ok(await ToOffer(db, offer, me));
        });

        // A deal that went wrong: either side reports the other, once, in their own words. It counts on the other's record.
        offers.MapPost("/{id:guid}/report", async (Guid id, TradeReport report, ClaimsPrincipal player, TradeDb db) =>
        {
            var me = ListingEndpoints.PlayerId(player);
            var reason = report.Reason?.Trim() ?? "";
            if (reason.Length is 0 or > MaxReportLength)
                return Problem($"Say what went wrong in at most {MaxReportLength} characters.");
            var offer = await Offers(db).SingleOrDefaultAsync(o => o.Id == id && (o.BuyerId == me || o.Listing!.SellerId == me));
            if (offer is null)
                return Results.NotFound();
            if (offer.Status is not (OfferStatus.Accepted or OfferStatus.Cancelled))
                return Results.Problem("Only a deal that was accepted can be reported.", statusCode: StatusCodes.Status409Conflict);
            if (offer.BuyerId == me)
                offer.BuyerReport ??= reason;
            else
                offer.SellerReport ??= reason;
            if (await SaveOrConflict(db) is { } conflict)
                return conflict;
            await Moderation.CheckAsync(db, Other(offer, me));
            return Results.Ok(await ToOffer(db, offer, me));
        });
    }

    /// <summary>
    /// Open offers lapse with a listing that expired, as with one sold or withdrawn: the player's own, and those on their
    /// listings, are declined when they next look (the Offers poll, My listings). Nothing could come of them: an expired
    /// listing can't be accepted on, and a renewed one starts without them.
    /// </summary>
    internal static Task<int> DeclineExpired(TradeDb db, Guid me, DateTimeOffset now) => db.Offers
        .Where(o => (o.BuyerId == me || o.Listing!.SellerId == me) && (o.Status == OfferStatus.Pending || o.Status == OfferStatus.Countered)
                    && o.Listing!.Status == ListingStatus.Active && o.Listing.Expires <= now)
        .ExecuteUpdateAsync(o => o.SetProperty(x => x.Status, OfferStatus.Declined).SetProperty(x => x.Updated, now));

    private static IQueryable<OfferRow> Offers(TradeDb db) => db.Offers
        .Include(o => o.Buyer).Include(o => o.Listing!).ThenInclude(l => l.Seller)
        .Include(o => o.Listing!).ThenInclude(l => l.Affixes).AsSplitQuery();

    /// <summary>Between 1 gold and the listing's asking price (any amount up to the maximum on a best-offer listing).</summary>
    private static bool FitsAsking(long price, ListingRow listing) =>
        price > 0 && price <= ListingRules.MaxPriceGold && price <= (listing.PriceGold ?? long.MaxValue);

    /// <summary>Closes an open (pending or countered) offer: the seller declines it, or the buyer withdraws it.</summary>
    private static async Task<IResult> Move(TradeDb db, Guid me, Guid id, bool seller, OfferStatus to)
    {
        var offer = await Offers(db).SingleOrDefaultAsync(o => o.Id == id && (seller ? o.Listing!.SellerId == me : o.BuyerId == me));
        if (offer is null)
            return Results.NotFound();
        if (offer.Status is not (OfferStatus.Pending or OfferStatus.Countered))
            return Results.Problem("This offer is no longer open.", statusCode: StatusCodes.Status409Conflict);
        offer.Status = to;
        offer.Updated = DateTimeOffset.UtcNow;
        if (await SaveOrConflict(db) is { } conflict)
            return conflict;
        return Results.Ok(await ToOffer(db, offer, me));
    }

    /// <summary>Saves the offer's change, or 409 when someone changed the offer since it was read (the later change loses).</summary>
    private static async Task<IResult?> SaveOrConflict(TradeDb db)
    {
        try
        {
            await db.SaveChangesAsync();
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem("This offer just changed; look again and retry.", statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static IResult Problem(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["offer"] = [message] });

    private static Guid Other(OfferRow offer, Guid me) => offer.BuyerId == me ? offer.Listing!.SellerId : offer.BuyerId;

    /// <summary>
    /// Trading records of these users: deals where the other side marked the deal completed, and reports the other
    /// side made against them.
    /// </summary>
    internal static async Task<Dictionary<Guid, TraderRecord>> Records(TradeDb db, IReadOnlyList<Guid> users)
    {
        var rows = await db.Offers
            .Where(o => (users.Contains(o.BuyerId) || users.Contains(o.Listing!.SellerId))
                        && (o.BuyerDone || o.SellerDone || o.BuyerReport != null || o.SellerReport != null))
            .Select(o => new { o.BuyerId, o.Listing!.SellerId, o.BuyerDone, o.SellerDone, BuyerReported = o.BuyerReport != null, SellerReported = o.SellerReport != null })
            .ToListAsync();
        return users.ToDictionary(u => u, u => new TraderRecord(
            rows.Count(r => (r.BuyerId == u && r.SellerDone) || (r.SellerId == u && r.BuyerDone)),
            rows.Count(r => (r.BuyerId == u && r.SellerReported) || (r.SellerId == u && r.BuyerReported))));
    }

    internal static async Task<Offer> ToOffer(TradeDb db, OfferRow row, Guid me) => ToOffer(row, me, await Records(db, [Other(row, me)]));

    /// <summary>
    /// The offer as <paramref name="me"/> sees it: the other side's record and whether they're in game always; their
    /// BattleTag, and their zone if they share it, only once the offer is accepted.
    /// </summary>
    private static Offer ToOffer(OfferRow row, Guid me, Dictionary<Guid, TraderRecord> records)
    {
        var mySale = row.Listing!.SellerId == me;
        var other = mySale ? row.Buyer : row.Listing.Seller;
        var accepted = row.Status == OfferStatus.Accepted;
        return new Offer(row.Id, ListingEndpoints.ToListing(row.Listing, revealSeller: mySale), row.PriceGold, row.Status, row.Created, row.Updated,
            accepted ? other?.BattleTag : null,
            row.CounterGold, records.GetValueOrDefault(Other(row, me)), mySale ? row.SellerDone : row.BuyerDone,
            PresenceEndpoints.IsInGame(other), accepted ? PresenceEndpoints.ZoneOf(other) : null);
    }
}
