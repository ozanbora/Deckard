using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Deckard.Trade.Api;

/// <summary>
/// One row per sign-in. A token works only while its row is there: signing out or deleting the account removes it, so the
/// token stops at once on the instance that removed it and within <see cref="CacheFor"/> on the others. Rows that ran out
/// go when someone signs in and in <see cref="RetentionCleanup"/>'s daily pass. Rows made by the development
/// sign-in are refused outside development, so a development database can't hand out sessions to a live server.
/// A ban keeps the session: trading already refuses banned players, and they can still download or delete their data.
/// </summary>
public static class Sessions
{
    public const string Claim = "session";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    public static async Task<AuthEndpoints.Session> StartAsync(TradeDb db, TokenIssuer tokens, UserRow user, bool devLogin)
    {
        var now = DateTimeOffset.UtcNow;
        await db.Sessions.Where(s => s.Expires < now).ExecuteDeleteAsync(); // the ones that ran out
        var row = new SessionRow { Id = Guid.NewGuid(), UserId = user.Id, Created = now, Expires = now + TokenIssuer.Lifetime, DevLogin = devLogin };
        db.Sessions.Add(row);
        await db.SaveChangesAsync();
        return new(tokens.Issue(user, row.Id, row.Expires), user.BattleTag, row.Expires, user.Id);
    }

    /// <summary>Runs after a token's signature checks out: fails it when its session has ended.</summary>
    public static async Task CheckAsync(TokenValidatedContext context)
    {
        var services = context.HttpContext.RequestServices;
        if (!Guid.TryParse(context.Principal?.FindFirstValue(Claim), out var id)) // a token from before sessions
        {
            context.Fail("Sign in again.");
            return;
        }
        var cache = services.GetRequiredService<IMemoryCache>();
        if (!cache.TryGetValue(Key(id), out bool live))
        {
            var development = services.GetRequiredService<IHostEnvironment>().IsDevelopment();
            live = await services.GetRequiredService<TradeDb>().Sessions
                .AnyAsync(s => s.Id == id && s.Expires > DateTimeOffset.UtcNow && (development || !s.DevLogin));
            cache.Set(Key(id), live, CacheFor);
        }
        if (!live)
            context.Fail("This session has ended.");
    }

    public static async Task EndAsync(TradeDb db, IMemoryCache cache, ClaimsPrincipal player)
    {
        if (!Guid.TryParse(player.FindFirstValue(Claim), out var id))
            return;
        await db.Sessions.Where(s => s.Id == id).ExecuteDeleteAsync();
        cache.Remove(Key(id));
    }

    /// <summary>Ends all of a player's sessions (deleting the account).</summary>
    public static async Task EndAllAsync(TradeDb db, IMemoryCache cache, Guid userId)
    {
        foreach (var id in await db.Sessions.Where(s => s.UserId == userId).Select(s => s.Id).ToListAsync())
            cache.Remove(Key(id));
        await db.Sessions.Where(s => s.UserId == userId).ExecuteDeleteAsync();
    }

    private static string Key(Guid id) => $"session:{id}";
}
