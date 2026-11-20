using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Deckard.Trade.Api;

/// <summary>
/// Development databases made before migrations (by EnsureCreated, then patched table by table) have the tables but no
/// migration history, so the first migration would fail creating them again. This brings such a database up to the
/// first migration's schema and records it as applied; later migrations then run as usual. Production databases are
/// made by migrations from the start and never need it.
/// </summary>
public static class DevelopmentSchema
{
    /// <summary>Whether the connection string's database is on this machine.</summary>
    public static bool IsLocal(string connectionString) =>
        new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Host?.Split(',').All(host => host.Trim() is "localhost" or "127.0.0.1" or "::1") == true;

    public static async Task AdoptAsync(TradeDb db)
    {
        if (!await db.Database.CanConnectAsync() || !await Exists(db, "users") || await Exists(db, HistoryRepository.DefaultTableName))
            return; // no database yet or a new one (migrations make it), or one migrations already manage
        // What the old start-up did: create the tables and columns added since the database was made.
        await db.Database.ExecuteSqlRawAsync(System.Text.RegularExpressions.Regex.Replace(db.Database.GenerateCreateScript(),
            @"CREATE (TABLE|INDEX|UNIQUE INDEX) ", "CREATE $1 IF NOT EXISTS "));
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE patrons ADD COLUMN IF NOT EXISTS "EarlyAccess" boolean NOT NULL DEFAULT false;
            ALTER TABLE patrons ADD COLUMN IF NOT EXISTS "CreditName" text;
            """);
        var history = db.GetService<IHistoryRepository>();
        var initial = db.Database.GetMigrations().First();
        await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript());
        await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(initial, ProductInfo.GetVersion())));
    }

    private static async Task<bool> Exists(TradeDb db, string table)
    {
        var quoted = $"\"{table}\""; // as created: "__EFMigrationsHistory" keeps its capitals
        return await db.Database.SqlQuery<bool>($"SELECT to_regclass({quoted}) IS NOT NULL AS \"Value\"").SingleAsync();
    }
}
