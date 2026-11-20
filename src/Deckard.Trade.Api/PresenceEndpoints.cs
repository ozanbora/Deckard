using System.Security.Claims;
using Deckard.Core.Trade;
using Microsoft.EntityFrameworkCore;

namespace Deckard.Trade.Api;

/// <summary>
/// Presence: the app reports every poll whether the player is in Diablo IV, whether to show them as online, and, when
/// they share it, the zone they're in. Others see "in game" only for players who opt in; the zone only reaches the
/// player's accepted trade partners (see <see cref="OfferEndpoints"/>), and stops being shown once it's stale.
/// </summary>
public static class PresenceEndpoints
{
    /// <summary>A player counts as online this long after their last poll (the app polls every 15 s).</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(2);

    /// <summary>A shared zone is shown this long after it was last read.</summary>
    public static readonly TimeSpan ZoneShown = TimeSpan.FromMinutes(10);

    public const int MaxZoneLength = 60;

    public static void MapPresenceEndpoints(this WebApplication app)
    {
        app.MapPost("/presence", async (Presence presence, ClaimsPrincipal player, TradeDb db) =>
        {
            var me = await ListingEndpoints.CurrentUser(player, db);
            if (me is null)
                return Results.Forbid();
            var now = DateTimeOffset.UtcNow;
            me.LastSeen = now;
            me.InGame = presence.InGame;
            me.ShowOnline = presence.ShowOnline;
            // Sharing off clears the zone at once; a zone is only kept while the player is in game.
            var zone = presence.ShareZone && presence.InGame ? presence.Zone?.Trim() : null;
            me.Zone = zone is { Length: > 0 } ? zone[..Math.Min(zone.Length, MaxZoneLength)] : null;
            me.ZoneAt = me.Zone is null ? null : now;
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireRateLimiting("player");
    }

    /// <summary>Whether the user opted to show as online and is in the game now.</summary>
    public static bool IsInGame(UserRow? user) =>
        user is { ShowOnline: true, InGame: true, LastSeen: { } seen } && DateTimeOffset.UtcNow - seen < OnlineWindow;

    /// <summary>The user's zone if they share it and it's fresh.</summary>
    public static string? ZoneOf(UserRow? user) =>
        user is { Zone: { } zone, ZoneAt: { } at } && DateTimeOffset.UtcNow - at < ZoneShown && IsInGameIgnoringOptIn(user) ? zone : null;

    private static bool IsInGameIgnoringOptIn(UserRow user) =>
        user is { InGame: true, LastSeen: { } seen } && DateTimeOffset.UtcNow - seen < OnlineWindow;
}
