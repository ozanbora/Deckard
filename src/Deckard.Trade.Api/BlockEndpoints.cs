using System.Security.Claims;
using Deckard.Core.Trade;
using Microsoft.EntityFrameworkCore;

namespace Deckard.Trade.Api;

/// <summary>
/// Blocking: a player hides another by one of their listings, offers or buy orders, never learning who it is. It works
/// both ways: neither sees the other's listings or buy orders, nor can make the other an offer, and the open offers
/// between them end. Accepted deals stand; either side can still cancel one.
/// </summary>
public static class BlockEndpoints
{
    public const int MaxBlocks = 500;

    public static void MapBlockEndpoints(this WebApplication app)
    {
        var blocks = app.MapGroup("/blocks").RequireRateLimiting("player");

        blocks.MapPost("/", async (BlockRequest request, ClaimsPrincipal player, TradeDb db) =>
        {
            var me = ListingEndpoints.PlayerId(player);
            Guid? other = request switch
            {
                { ListingId: { } id } => await db.Listings.Where(l => l.Id == id).Select(l => (Guid?)l.SellerId).SingleOrDefaultAsync(),
                { OfferId: { } id } => await db.Offers.Where(o => o.Id == id && (o.BuyerId == me || o.Listing!.SellerId == me))
                    .Select(o => (Guid?)(o.BuyerId == me ? o.Listing!.SellerId : o.BuyerId)).SingleOrDefaultAsync(),
                { WantedId: { } id } => await db.Wanted.Where(w => w.Id == id).Select(w => (Guid?)w.BuyerId).SingleOrDefaultAsync(),
                _ => null,
            };
            if (other is not { } blocked || blocked == me)
                return Results.NotFound();
            if (await db.Blocks.CountAsync(b => b.BlockerId == me) >= MaxBlocks)
                return Results.Problem($"You can block at most {MaxBlocks} players.", statusCode: StatusCodes.Status409Conflict);
            if (!await db.Blocks.AnyAsync(b => b.BlockerId == me && b.BlockedId == blocked))
            {
                db.Blocks.Add(new BlockRow { BlockerId = me, BlockedId = blocked, Created = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }
            var now = DateTimeOffset.UtcNow;
            await db.Offers.Where(o => o.BuyerId == blocked && o.Listing!.SellerId == me && (o.Status == OfferStatus.Pending || o.Status == OfferStatus.Countered))
                .ExecuteUpdateAsync(o => o.SetProperty(x => x.Status, OfferStatus.Declined).SetProperty(x => x.Updated, now));
            await db.Offers.Where(o => o.BuyerId == me && o.Listing!.SellerId == blocked && (o.Status == OfferStatus.Pending || o.Status == OfferStatus.Countered))
                .ExecuteUpdateAsync(o => o.SetProperty(x => x.Status, OfferStatus.Withdrawn).SetProperty(x => x.Updated, now));
            return Results.NoContent();
        });

        // How many players the player blocked (never who: some were only ever a listing to them).
        blocks.MapGet("/", async (ClaimsPrincipal player, TradeDb db) =>
        {
            var me = ListingEndpoints.PlayerId(player);
            return Results.Ok(await db.Blocks.CountAsync(b => b.BlockerId == me));
        });

        blocks.MapDelete("/", async (ClaimsPrincipal player, TradeDb db) =>
        {
            var me = ListingEndpoints.PlayerId(player);
            await db.Blocks.Where(b => b.BlockerId == me).ExecuteDeleteAsync();
            return Results.NoContent();
        });
    }

    /// <summary>The players hidden from <paramref name="me"/>: those they blocked and those who blocked them.</summary>
    internal static IQueryable<Guid> Hidden(TradeDb db, Guid me) =>
        db.Blocks.Where(b => b.BlockerId == me).Select(b => b.BlockedId)
            .Concat(db.Blocks.Where(b => b.BlockedId == me).Select(b => b.BlockerId));
}
