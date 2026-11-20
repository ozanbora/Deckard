using Deckard.Core.Trade;
using Microsoft.EntityFrameworkCore;

namespace Deckard.Trade.Api;

/// <summary>
/// The report queue. A player reported by <see cref="FlagReporters"/> or more different trade partners, with more
/// reports than completed trades, is flagged: their listings and buy orders leave the market until a moderator looks,
/// from the server's command line: <c>--reports</c> lists the queue, <c>--unflag &lt;BattleTag&gt;</c> puts a player back,
/// <c>--ban &lt;BattleTag&gt;</c> bans them (their listings and orders stay hidden, and they can't trade).
/// <c>--uncredit &lt;BattleTag&gt;</c> takes a patron's name out of the app's credits (they can choose a new one).
/// <c>--nofeedback &lt;BattleTag&gt;</c> stops a player sending feedback; <c>--feedback &lt;report&gt;</c> copies a
/// report's screenshots to a folder for whoever works on its issue.
/// </summary>
public static class Moderation
{
    public const int FlagReporters = 3;

    /// <summary>Flags the player when their reports call for it; called after each report against them.</summary>
    public static async Task CheckAsync(TradeDb db, Guid user)
    {
        if (await db.Flags.AnyAsync(f => f.UserId == user))
            return;
        var reporters = await db.Offers.Where(o => o.BuyerId == user && o.SellerReport != null).Select(o => o.Listing!.SellerId)
            .Concat(db.Offers.Where(o => o.Listing!.SellerId == user && o.BuyerReport != null).Select(o => o.BuyerId))
            .Distinct().CountAsync();
        if (reporters < FlagReporters)
            return;
        var record = (await OfferEndpoints.Records(db, [user]))[user];
        if (record.Reports <= record.Trades)
            return;
        db.Flags.Add(new FlagRow { UserId = user, Created = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    /// <summary>Players off the market: flagged for review, or banned.</summary>
    internal static IQueryable<Guid> OffMarket(TradeDb db) =>
        db.Flags.Select(f => f.UserId).Concat(db.Users.Where(u => u.Banned).Select(u => u.Id));

    /// <summary>Whether any of these players is off the market: then no offer, deal or sale between them goes ahead.</summary>
    internal static Task<bool> AnyOffMarketAsync(TradeDb db, params Guid[] players) => OffMarket(db).AnyAsync(id => players.Contains(id));

    /// <summary>
    /// Runs a moderation command given on the command line, if any; true when one ran (the server then exits).
    /// </summary>
    public static async Task<bool> RunCommandAsync(TradeDb db, string[] args, TextWriter output, string? feedbackRoot = null)
    {
        if (Array.IndexOf(args, "--feedback") is var report and >= 0)
        {
            var id = report + 1 < args.Length ? args[report + 1] : "";
            var folder = feedbackRoot is null ? null : Path.Combine(feedbackRoot, id);
            if (!int.TryParse(id, out _) || folder is null || !Directory.Exists(folder))
            {
                output.WriteLine($"Report {id} has no screenshots on this server.");
                return true;
            }
            var copy = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"deckard-feedback-{id}"));
            foreach (var file in Directory.GetFiles(folder))
                File.Copy(file, Path.Combine(copy.FullName, Path.GetFileName(file)), overwrite: true);
            output.WriteLine($"Report {id}'s screenshots: {copy.FullName}");
            return true;
        }
        if (Array.IndexOf(args, "--nofeedback") is var mute and >= 0)
        {
            var tag = mute + 1 < args.Length ? args[mute + 1] : "";
            if (await db.Users.SingleOrDefaultAsync(u => u.BattleTag == tag) is not { } muted)
            {
                output.WriteLine($"No player {tag}.");
                return true;
            }
            if (!await db.FeedbackMutes.AnyAsync(m => m.UserId == muted.Id))
            {
                db.FeedbackMutes.Add(new FeedbackMuteRow { UserId = muted.Id });
                await db.SaveChangesAsync();
            }
            output.WriteLine($"{tag} can no longer send feedback.");
            return true;
        }
        if (args.Contains("--reports"))
        {
            var flagged = await db.Flags.OrderBy(f => f.Created).ToListAsync();
            var users = await db.Users.Where(u => flagged.Select(f => f.UserId).Contains(u.Id)).ToDictionaryAsync(u => u.Id);
            var records = await OfferEndpoints.Records(db, [.. flagged.Select(f => f.UserId)]);
            output.WriteLine(flagged.Count == 0 ? "No players are waiting for review." : $"{flagged.Count} player(s) waiting for review:");
            foreach (var flag in flagged)
            {
                var record = records[flag.UserId];
                output.WriteLine($"{users[flag.UserId].BattleTag}  flagged {flag.Created:yyyy-MM-dd HH:mm}  {record.Trades} trades, {record.Reports} reports");
                var reasons = await db.Offers.Where(o => o.BuyerId == flag.UserId && o.SellerReport != null).Select(o => o.SellerReport!)
                    .Concat(db.Offers.Where(o => o.Listing!.SellerId == flag.UserId && o.BuyerReport != null).Select(o => o.BuyerReport!))
                    .ToListAsync();
                foreach (var reason in reasons)
                    output.WriteLine($"    \"{reason}\"");
            }
            return true;
        }
        if (Array.IndexOf(args, "--uncredit") is var credit and >= 0)
        {
            var tag = credit + 1 < args.Length ? args[credit + 1] : "";
            var cleared = await db.Patrons.Where(p => db.Users.Any(u => u.Id == p.UserId && u.BattleTag == tag) && p.CreditName != null)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.CreditName, (string?)null));
            output.WriteLine(cleared > 0 ? $"{tag} is no longer named in the credits." : $"{tag} has no name in the credits.");
            return true;
        }
        foreach (var (command, ban) in new[] { ("--unflag", false), ("--ban", true) })
        {
            if (Array.IndexOf(args, command) is not (var at and >= 0))
                continue;
            var tag = at + 1 < args.Length ? args[at + 1] : "";
            var user = await db.Users.SingleOrDefaultAsync(u => u.BattleTag == tag);
            if (user is null)
            {
                output.WriteLine($"No player {tag}.");
                return true;
            }
            await db.Flags.Where(f => f.UserId == user.Id).ExecuteDeleteAsync();
            if (ban)
            {
                user.Banned = true;
                await db.SaveChangesAsync();
            }
            output.WriteLine(ban ? $"Banned {tag}." : $"{tag} is back on the market.");
            return true;
        }
        return false;
    }
}
