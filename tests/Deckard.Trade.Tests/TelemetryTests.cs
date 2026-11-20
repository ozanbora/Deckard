using System.Net;
using System.Net.Http.Json;
using Deckard.Core.Trade;
using Deckard.Trade.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Deckard.Trade.Tests;

[Collection("TradeDb")]
public class TelemetryTests
{
    [DatabaseFact]
    public async Task KeepsOnlyAllowlistedEventsAndValues()
    {
        using var api = new TradeApi();
        var player = await api.SignedIn("Player#1234");
        var install = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        TelemetryBatch Batch(params TelemetryEvent[] events) => new(install, "1.4.0", "Windows 11 26100", "win", events);

        var sent = await player.PostAsJsonAsync("/telemetry", Batch(
            new TelemetryEvent("tooltip_read", now, new Dictionary<string, string> { ["matched"] = "7", ["total"] = "8", ["battletag"] = "Player#1234" }),
            new TelemetryEvent("tooltip_read", now),
            new TelemetryEvent("crash", now, new Dictionary<string, string> { ["type"] = "NullReferenceException", ["stack"] = new string('s', 5000) }),
            new TelemetryEvent("keylogger", now),                          // not on the allowlist
            new TelemetryEvent("session_start", now - TimeSpan.FromDays(30)))); // too old
        Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeDb>();
        var rows = await db.Telemetry.OrderBy(t => t.Id).ToListAsync();
        Assert.Equal(["tooltip_read", "tooltip_read", "crash"], rows.Select(r => r.Name));
        Assert.All(rows, r => Assert.Equal(install, r.InstallId));
        Assert.DoesNotContain("battletag", rows[0].Props);                 // values not on the event's list are dropped
        Assert.Contains("\"matched\":\"7\"", rows[0].Props);
        Assert.Null(rows[1].Props);
        Assert.True(rows[2].Props!.Length < TelemetryEndpoints.MaxStack + 100);
        var daily = await db.TelemetryDaily.ToDictionaryAsync(d => d.Name, d => d.Count);
        Assert.Equal(2, daily["tooltip_read"]);

        Assert.Equal(HttpStatusCode.BadRequest, (await player.PostAsJsonAsync("/telemetry", Batch())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await player.PostAsJsonAsync("/telemetry", Batch([.. Enumerable.Repeat(new TelemetryEvent("card_shown", now), TelemetryEndpoints.MaxEvents + 1)]))).StatusCode);

        // Raw events go after 90 days; the counts stay.
        await db.Telemetry.ExecuteUpdateAsync(t => t.SetProperty(x => x.At, now - TelemetryEndpoints.KeepRaw - TimeSpan.FromDays(1)));
        await RetentionCleanup.RunAsync(db, now);
        Assert.Empty(await db.Telemetry.ToListAsync());
        Assert.Equal(2, (await db.TelemetryDaily.SingleAsync(d => d.Name == "tooltip_read")).Count);
    }
}
