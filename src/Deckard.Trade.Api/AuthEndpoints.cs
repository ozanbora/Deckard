using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;

namespace Deckard.Trade.Api;

public static partial class AuthEndpoints
{
    public sealed record DevLogin(string BattleTag);
    /// <param name="UserId">The player's Deckard id: stable across BattleTag renames, so the app keeps per-account settings under it.</param>
    public sealed record Session(string Token, string BattleTag, DateTimeOffset Expires, Guid UserId);

    public static void MapAuthEndpoints(this WebApplication app)
    {
        // Signing out ends the session on the server too, so a copy of the token stops working.
        app.MapDelete("/auth/session", async (ClaimsPrincipal player, TradeDb db, IMemoryCache cache) =>
        {
            await Sessions.EndAsync(db, cache, player);
            return Results.NoContent();
        }).RequireRateLimiting("login");

        // shortcut: development-only stand-in for Battle.net login (OAuth with PKCE from Deckard, which verifies the
        // BattleTag); replace it once the Battle.net client is registered. Never mapped outside development.
        if (!app.Environment.IsDevelopment())
            return;
        app.MapPost("/auth/dev-login", async (DevLogin request, TradeDb db, TokenIssuer tokens) =>
        {
            if (!BattleTag().IsMatch(request.BattleTag))
                return Results.BadRequest("A BattleTag looks like Name#1234.");
            var user = await db.Users.SingleOrDefaultAsync(u => u.BattleTag == request.BattleTag);
            if (user is null)
            {
                user = new UserRow { Id = Guid.NewGuid(), BattleTag = request.BattleTag, Created = DateTimeOffset.UtcNow };
                db.Users.Add(user);
                await db.SaveChangesAsync();
            }
            if (user.Banned)
                return Results.Forbid();
            return Results.Ok(await Sessions.StartAsync(db, tokens, user, devLogin: true));
        }).AllowAnonymous().RequireRateLimiting("login");
    }

    [GeneratedRegex(@"^\p{L}[\p{L}\p{N}]{2,11}#\d{4,6}$")]
    private static partial Regex BattleTag();
}
