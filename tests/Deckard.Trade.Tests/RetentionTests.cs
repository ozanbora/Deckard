using System.Net.Http.Json;
using Deckard.Core.Trade;
using Deckard.Trade.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Deckard.Trade.Tests.Fixtures;

namespace Deckard.Trade.Tests;

[Collection("TradeDb")]
public class RetentionTests
{
    [DatabaseFact]
    public async Task DeletesTradesThatEndedOverAYearAgo()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");
        var buyer = await api.SignedIn("Buyer#5678");
        async Task<Listing> List(double crit) => (await (await seller.PostAsJsonAsync("/listings", Ring(crit: crit))).Content.ReadFromJsonAsync<Listing>())!;
        var oldSold = await List(1);
        var oldExpired = await List(2);
        var oldReserved = await List(3);
        var recentSold = await List(4);
        var deal = (await (await buyer.PostAsJsonAsync($"/listings/{oldSold.Id}/offers", new NewOffer(1_000_000))).Content.ReadFromJsonAsync<Offer>())!;
        await seller.PostAsync($"/offers/{deal.Id}/accept", null);
        await seller.PostAsync($"/listings/{oldSold.Id}/sold", null);
        await buyer.PostAsJsonAsync($"/offers/{deal.Id}/report", new TradeReport("late"));
        var open = (await (await buyer.PostAsJsonAsync($"/listings/{oldReserved.Id}/offers", new NewOffer(1_000_000))).Content.ReadFromJsonAsync<Offer>())!;
        await seller.PostAsync($"/offers/{open.Id}/accept", null); // an open deal: kept however old
        await seller.PostAsync($"/listings/{recentSold.Id}/sold", null);
        await buyer.PostAsJsonAsync("/wanted", new NewWanted(new ListingSearch(Realm.Eternal(), "Ring"), 1_000_000));

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeDb>();
        var longAgo = DateTimeOffset.UtcNow - RetentionCleanup.Keep - TimeSpan.FromDays(30);
        foreach (var id in new[] { oldSold.Id, oldExpired.Id, oldReserved.Id })
            await db.Listings.Where(l => l.Id == id).ExecuteUpdateAsync(l => l.SetProperty(x => x.Expires, longAgo).SetProperty(x => x.SoldAt, x => x.SoldAt == null ? null : longAgo));
        await db.Wanted.ExecuteUpdateAsync(w => w.SetProperty(x => x.Expires, longAgo));

        Assert.Equal((2, 1), await RetentionCleanup.RunAsync(db, DateTimeOffset.UtcNow));

        Assert.Equal(new HashSet<Guid> { oldReserved.Id, recentSold.Id }, (await db.Listings.Select(l => l.Id).ToListAsync()).ToHashSet());
        Assert.False(await db.Offers.AnyAsync(o => o.Id == deal.Id));     // the sold listing's deal and its report went with it
        Assert.True(await db.Offers.AnyAsync(o => o.Id == open.Id));
        Assert.False(await db.ListingAffixes.AnyAsync(a => a.ListingId == oldSold.Id));
        Assert.Empty(await db.Wanted.ToListAsync());
    }

    [DatabaseFact]
    public async Task DeletesSessionsThatRanOut()
    {
        using var api = new TradeApi();
        await api.SignedIn("Gone#1234");
        await api.SignedIn("Here#5678");
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeDb>();
        var gone = await db.Users.SingleAsync(u => u.BattleTag == "Gone#1234");
        await db.Sessions.Where(s => s.UserId == gone.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Expires, DateTimeOffset.UtcNow.AddMinutes(-1)));

        await RetentionCleanup.RunAsync(db, DateTimeOffset.UtcNow);

        Assert.Equal(["Here#5678"], await db.Sessions.Select(s => db.Users.Single(u => u.Id == s.UserId).BattleTag).ToListAsync());
    }
}
