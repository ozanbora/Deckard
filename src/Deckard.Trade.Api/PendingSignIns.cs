using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Deckard.Trade.Api;

/// <summary>
/// Sign-ins and Patreon links in progress, kept in the database so any server instance can finish them: the provider's
/// callback and the app's exchange may reach another instance than the one that started the sign-in. Each works once:
/// taking one deletes it, in the same statement.
/// </summary>
public static class PendingSignIns
{
    public static async Task PutAsync<T>(TradeDb db, string key, T value, TimeSpan lifetime)
    {
        var now = DateTimeOffset.UtcNow;
        await db.PendingSignIns.Where(p => p.Expires < now).ExecuteDeleteAsync(); // the ones nobody finished
        db.PendingSignIns.Add(new PendingSignInRow { Key = key, Payload = JsonSerializer.Serialize(value), Expires = now + lifetime });
        await db.SaveChangesAsync();
    }

    /// <summary>The value under <paramref name="key"/>, removed; null when there's none, it expired, or it was taken already.</summary>
    public static async Task<T?> TakeAsync<T>(TradeDb db, string key) where T : class
    {
        // One statement reads and deletes, so two requests (two instances, or a replayed link) can't both take it.
        var payloads = await db.Database.SqlQuery<string>(
            $"""DELETE FROM pending_sign_ins WHERE "Key" = {key} AND "Expires" > {DateTimeOffset.UtcNow} RETURNING "Payload" AS "Value" """).ToListAsync();
        return payloads is [var json] ? JsonSerializer.Deserialize<T>(json) : null;
    }
}
