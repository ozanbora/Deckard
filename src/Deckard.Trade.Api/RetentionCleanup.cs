using Deckard.Core.Trade;
using Microsoft.EntityFrameworkCore;

namespace Deckard.Trade.Api;

/// <summary>
/// Keeps what the privacy policy (docs/privacy-policy.md) promises: once a day, deletes listings that ended more than
/// <see cref="Keep"/> ago, with their affixes, offers and the reports on those deals (the database cascades them), and
/// buy orders that ended as long ago. A listing ends when it sells, or when it expires or is withdrawn (it has no
/// withdrawal time, so its expiry, at most 14 days later, counts); a reserved one is an open deal and stays. Trading
/// records then count the last year. Raw usage events (<see cref="TelemetryEndpoints"/>) go after 90 days, and feedback
/// reports a year after they were sent, once their screenshots are gone.
/// </summary>
public sealed class RetentionCleanup(IServiceScopeFactory scopes, ILogger<RetentionCleanup> log) : BackgroundService
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(365);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TradeDb>();
                // One instance a day: the others find the lock held and wait for tomorrow.
                await SingleInstance.TryRunAsync(db, SingleInstance.Retention, async () =>
                {
                    var (listings, orders) = await RunAsync(db, DateTimeOffset.UtcNow, stoppingToken);
                    if (listings + orders > 0)
                        log.LogInformation("Retention: deleted {Listings} listings and {Orders} buy orders that ended over a year ago.", listings, orders);
                }, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Retention cleanup failed; it runs again tomorrow.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public static async Task<(int Listings, int Orders)> RunAsync(TradeDb db, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var before = now - Keep;
        var listings = await db.Listings
            .Where(l => (l.Status == ListingStatus.Sold && (l.SoldAt ?? l.Expires) < before)
                        || ((l.Status == ListingStatus.Active || l.Status == ListingStatus.Withdrawn) && l.Expires < before))
            .ExecuteDeleteAsync(cancellationToken);
        // Any buy order has ended by its expiry: filled, cancelled or still open by then.
        var orders = await db.Wanted.Where(w => w.Expires < before).ExecuteDeleteAsync(cancellationToken);
        // Raw usage events go after 90 days; their daily counts stay.
        var oldEvents = now - TelemetryEndpoints.KeepRaw;
        await db.Telemetry.Where(t => t.At < oldEvents).ExecuteDeleteAsync(cancellationToken);
        // Who sent a report, a year on, once its screenshots are gone (GitHubIssues deletes those); the issue names no one.
        await db.Feedback.Where(f => f.Created < before && f.Screenshots == 0).ExecuteDeleteAsync(cancellationToken);
        // Sessions that ran out, also on a day nobody signs in (the privacy policy says 7 days at most).
        await db.Sessions.Where(s => s.Expires < now).ExecuteDeleteAsync(cancellationToken);
        return (listings, orders);
    }
}
