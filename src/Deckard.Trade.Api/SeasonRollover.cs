using System.Text.Json;
using Deckard.Core.Trade;
using Microsoft.EntityFrameworkCore;

namespace Deckard.Trade.Api;

/// <summary>
/// The market follows the game at a season's end: seasonal characters and their items move to the eternal realm
/// (hardcore stays hardcore), so the server, started on the new season's data, moves the old seasons' live listings
/// and open buy orders there too. Offers and deals on them stand: both players moved with their items.
/// </summary>
public static class SeasonRollover
{
    /// <returns>How many listings and buy orders moved.</returns>
    public static async Task<int> RunAsync(TradeDb db, int season)
    {
        var listings = await db.Listings
            .Where(l => l.Realm == RealmKind.Seasonal && l.Season < season && (l.Status == ListingStatus.Active || l.Status == ListingStatus.Reserved))
            .ExecuteUpdateAsync(l => l.SetProperty(x => x.Realm, RealmKind.Eternal).SetProperty(x => x.Season, 0));
        // A buy order keeps its realm in its search too, so it's moved row by row.
        var orders = await db.Wanted.Where(w => w.Realm == RealmKind.Seasonal && w.Season < season && w.Status == WantedStatus.Open).ToListAsync();
        foreach (var order in orders)
        {
            var want = JsonSerializer.Deserialize<ListingSearch>(order.Want)!;
            order.Want = JsonSerializer.Serialize(want with { Realm = Realm.Eternal(order.Hardcore) });
            (order.Realm, order.Season) = (RealmKind.Eternal, 0);
        }
        await db.SaveChangesAsync();
        return listings + orders.Count;
    }
}
