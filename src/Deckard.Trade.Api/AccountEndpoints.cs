using System.Security.Claims;
using Deckard.Core.Trade;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Deckard.Trade.Api;

/// <summary>
/// The player's own data, for the privacy policy's promises without an e-mail: GET /account/export gives everything the
/// server keeps about them as JSON, and DELETE /account removes it. A banned or flagged account keeps only its Battle.net
/// id, BattleTag and the ban or flag, so deleting it can't undo a ban; everything else goes. Usage statistics aren't
/// tied to an account, so they're neither exported nor deleted here.
/// </summary>
public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        var account = app.MapGroup("/account").RequireRateLimiting("player");

        account.MapGet("/export", async (ClaimsPrincipal player, TradeDb db) =>
        {
            var me = ListingEndpoints.PlayerId(player);
            if (await db.Users.FindAsync(me) is not { } user)
                return Results.NotFound();
            var listings = await db.Listings.Include(l => l.Affixes).Where(l => l.SellerId == me).OrderBy(l => l.Created).ToListAsync();
            var made = await db.Offers.Include(o => o.Listing).Where(o => o.BuyerId == me).OrderBy(o => o.Created).ToListAsync();
            var received = await db.Offers.Where(o => o.Listing!.SellerId == me).OrderBy(o => o.Created).ToListAsync();
            var patron = await db.Patrons.FindAsync(me);
            var record = (await OfferEndpoints.Records(db, [me]))[me];
            return Results.Ok(new
            {
                exported = DateTimeOffset.UtcNow,
                account = new
                {
                    user.BattleTag, signedInWithBattleNet = user.BattleNetId is not null, user.Created, user.Banned,
                    flaggedForReview = await db.Flags.AnyAsync(f => f.UserId == me),
                    presence = new { user.ShowOnline, user.InGame, user.LastSeen, user.Zone, user.ZoneAt },
                    tradingRecord = new { completedTrades = record.Trades, reports = record.Reports },
                    playersYouBlocked = await db.Blocks.CountAsync(b => b.BlockerId == me), // never who: some were only a listing
                },
                listings = listings.Select(l => new
                {
                    l.Id, realm = new Realm(l.Realm, l.Hardcore, l.Season), l.ItemType, l.Name, l.Rarity, l.ItemPower, l.Ancestral, l.AspectId,
                    affixes = l.Affixes.OrderBy(a => a.Index).Select(a => new { a.AffixId, a.Value, a.Greater, a.Slot }),
                    l.PriceGold, l.Quantity, status = l.Status.ToString(), l.Created, l.Expires, l.SoldPriceGold, l.SoldAt,
                }),
                offersYouMade = made.Select(o => new
                {
                    o.Id, o.ListingId, listing = o.Listing!.Name, o.PriceGold, o.CounterGold, status = o.Status.ToString(), o.Created, o.Updated,
                    youMarkedCompleted = o.BuyerDone, yourReport = o.BuyerReport,
                }),
                offersOnYourListings = received.Select(o => new
                {
                    o.Id, o.ListingId, o.PriceGold, o.CounterGold, status = o.Status.ToString(), o.Created, o.Updated,
                    youMarkedCompleted = o.SellerDone, yourReport = o.SellerReport,
                }),
                buyOrders = (await db.Wanted.Where(w => w.BuyerId == me).OrderBy(w => w.Created).ToListAsync()).Select(w => new
                {
                    w.Id, want = System.Text.Json.JsonSerializer.Deserialize<ListingSearch>(w.Want), w.PriceGold, status = w.Status.ToString(), w.Created, w.Expires,
                }),
                sessions = await db.Sessions.Where(s => s.UserId == me).OrderBy(s => s.Created).Select(s => new { s.Created, s.Expires }).ToListAsync(),
                patreon = patron is null ? null : new { patron.PatreonUserId, patron.Patron, patron.EarlyAccess, patron.CreditName, patron.CheckedAt },
                feedback = (await db.Feedback.Where(f => f.UserId == me).OrderBy(f => f.Id).ToListAsync()).Select(f => new
                {
                    reportId = f.Id, f.Kind, f.Title, f.Description, f.Diagnostics, screenshotsKept = f.Screenshots, f.Created, f.IssueUrl,
                }),
            });
        });

        account.MapDelete("/", async (ClaimsPrincipal player, TradeDb db, IConfiguration config, IMemoryCache cache) =>
        {
            var me = ListingEndpoints.PlayerId(player);
            if (await db.Users.FindAsync(me) is not { } user)
                return Results.NotFound();
            await using var transaction = await db.Database.BeginTransactionAsync();
            foreach (var report in await db.Feedback.Where(f => f.UserId == me && f.Screenshots > 0).Select(f => f.Id).ToListAsync())
            {
                var folder = Path.Combine(FeedbackEndpoints.Root(config), report.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
            await db.Feedback.Where(f => f.UserId == me).ExecuteDeleteAsync();
            await Sessions.EndAllAsync(db, cache, me);
            await db.Patrons.Where(p => p.UserId == me).ExecuteDeleteAsync();
            await db.Blocks.Where(b => b.BlockerId == me || b.BlockedId == me).ExecuteDeleteAsync();
            await db.Wanted.Where(w => w.BuyerId == me).ExecuteDeleteAsync();
            await db.Offers.Where(o => o.BuyerId == me).ExecuteDeleteAsync();
            await db.Listings.Where(l => l.SellerId == me).ExecuteDeleteAsync(); // with their affixes and the offers on them
            // A ban or a pending review outlives the account, so signing in again can't shake it off.
            if (user.Banned || await db.Flags.AnyAsync(f => f.UserId == me))
            {
                user.ShowOnline = user.InGame = false;
                (user.LastSeen, user.Zone, user.ZoneAt) = (null, null, null);
                await db.SaveChangesAsync();
            }
            else
            {
                await db.FeedbackMutes.Where(m => m.UserId == me).ExecuteDeleteAsync();
                await db.Users.Where(u => u.Id == me).ExecuteDeleteAsync();
            }
            await transaction.CommitAsync();
            return Results.NoContent();
        });
    }
}
