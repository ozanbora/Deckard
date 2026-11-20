using Deckard.Core.Trade;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Deckard.Trade.Api;

/// <summary>
/// Anonymous usage statistics, sent by the app only with the player's consent. The endpoint needs a signed-in player
/// like every other (the fallback policy), but stores no account and no IP address: only the installation's random id,
/// the app and Windows versions, and allowlisted events with allowlisted values. Raw events are kept 90 days
/// (<see cref="RetentionCleanup"/>); counts per day, event and app version stay.
/// </summary>
public static class TelemetryEndpoints
{
    public const int MaxEvents = 100, MaxBody = 64 * 1024, MaxStack = 2000, MaxValue = 100;
    public static readonly TimeSpan KeepRaw = TimeSpan.FromDays(90);
    private static readonly TimeSpan Oldest = TimeSpan.FromDays(7), Skew = TimeSpan.FromMinutes(5);

    /// <summary>The events the server accepts, and the values each may carry; anything else is dropped.</summary>
    private static readonly Dictionary<string, HashSet<string>> Allowed = new()
    {
        ["session_start"] = [],
        ["crash"] = ["type", "stack"],
        ["tooltip_read"] = ["matched", "total", "ocr2x", "width", "height", "dpi", "lang"],
        ["page_opened"] = ["page"],
        ["build_loaded"] = [],
        ["card_shown"] = [],
        ["listing_created"] = [],
    };

    public static void MapTelemetryEndpoints(this WebApplication app)
    {
        app.MapPost("/telemetry", async (TelemetryBatch batch, TradeDb db) =>
        {
            if (batch.InstallId == Guid.Empty || batch.Events is not { Count: > 0 and <= MaxEvents })
                return Results.BadRequest($"Send 1-{MaxEvents} events with an install id.");
            var now = DateTimeOffset.UtcNow;
            var (version, windows, channel) = (Clip(batch.AppVersion, 32), Clip(batch.Windows, 64), Clip(batch.Channel, 16));
            var rows = batch.Events
                .Where(e => Allowed.ContainsKey(e.Name) && e.At > now - Oldest && e.At < now + Skew)
                .Select(e => new TelemetryRow
                {
                    InstallId = batch.InstallId, Name = e.Name, At = e.At, AppVersion = version, Windows = windows, Channel = channel,
                    Props = Props(e) is { Count: > 0 } props ? System.Text.Json.JsonSerializer.Serialize(props) : null,
                })
                .ToList();
            if (rows.Count == 0)
                return Results.NoContent();
            db.Telemetry.AddRange(rows);
            await db.SaveChangesAsync();
            // The counts that outlive the raw events.
            foreach (var day in rows.GroupBy(r => (Day: DateOnly.FromDateTime(r.At.UtcDateTime), r.Name)))
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO telemetry_daily ("Day", "Name", "AppVersion", "Count") VALUES ({day.Key.Day}, {day.Key.Name}, {version}, {day.Count()})
                    ON CONFLICT ("Day", "Name", "AppVersion") DO UPDATE SET "Count" = telemetry_daily."Count" + EXCLUDED."Count"
                    """);
            return Results.NoContent();
        }).WithMetadata(new RequestSizeLimitAttribute(MaxBody)).RequireRateLimiting("player");
    }

    /// <summary>The event's allowlisted values, each cut to its length (a crash stack longer than the rest).</summary>
    private static Dictionary<string, string> Props(TelemetryEvent e) =>
        (e.Props ?? new Dictionary<string, string>())
            .Where(p => Allowed[e.Name].Contains(p.Key) && p.Value is not null)
            .ToDictionary(p => p.Key, p => Clip(p.Value, p.Key == "stack" ? MaxStack : MaxValue));

    private static string Clip(string? text, int length) => text is null ? "" : text.Length <= length ? text : text[..length];
}
