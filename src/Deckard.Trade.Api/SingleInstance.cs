using Microsoft.EntityFrameworkCore;

namespace Deckard.Trade.Api;

/// <summary>
/// Work only one server instance does at a time, under a PostgreSQL advisory lock: the schema's migrations at start (the
/// others wait), and the daily retention cleanup and the feedback filer (the others skip that round, so GitHub gets each
/// report once). The lock is the database session's: it goes when the work ends, or when a crashed instance's connection
/// closes.
/// </summary>
public static class SingleInstance
{
    // Arbitrary, fixed keys: one per kind of work.
    public const long Startup = 0x4445_434B_0001, Retention = 0x4445_434B_0002, Feedback = 0x4445_434B_0003;

    /// <summary>Waits for the lock, then does the work.</summary>
    public static async Task RunAsync(TradeDb db, long key, Func<Task> work, CancellationToken cancellationToken = default)
    {
        await db.Database.OpenConnectionAsync(cancellationToken); // one connection for the lock and the work
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock({key})", cancellationToken);
            try
            {
                await work();
            }
            finally
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({key})", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Does the work unless another instance is doing it; false when it skipped.</summary>
    public static async Task<bool> TryRunAsync(TradeDb db, long key, Func<Task> work, CancellationToken cancellationToken = default)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (!await db.Database.SqlQuery<bool>($"""SELECT pg_try_advisory_lock({key}) AS "Value" """).SingleAsync(cancellationToken))
                return false;
            try
            {
                await work();
                return true;
            }
            finally
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({key})", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
