using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Deckard.Trade.Api;

/// <summary>
/// For <c>dotnet ef migrations add &lt;Name&gt; --project src/Deckard.Trade.Api</c>: builds the model without starting the
/// server (no secrets or game data needed). Adding a migration never connects, so the connection string is a placeholder.
/// </summary>
public sealed class TradeDbDesignTime : IDesignTimeDbContextFactory<TradeDb>
{
    public TradeDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TradeDb>().UseNpgsql("Host=design-time-only").Options);
}
