using System.Net.Http.Json;
using Deckard.Core.Trade;
using Deckard.Trade.Api;
using Microsoft.Extensions.DependencyInjection;
using static Deckard.Trade.Tests.Fixtures;

namespace Deckard.Trade.Tests;

[Collection("TradeDb")]
public class SeasonRolloverTests
{
    [DatabaseFact]
    public async Task OldSeasonListingsAndBuyOrdersMoveToTheEternalRealm()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");
        var buyer = await api.SignedIn("Buyer#5678");
        var season = Realm.Seasonal(15, hardcore: true);
        var listing = (await (await seller.PostAsJsonAsync("/listings", Ring() with { Realm = season })).Content.ReadFromJsonAsync<Listing>())!;
        await buyer.PostAsJsonAsync($"/listings/{listing.Id}/offers", new NewOffer(20_000_000));
        await buyer.PostAsJsonAsync("/wanted", new NewWanted(new ListingSearch(season, "Ring"), 10_000_000));

        using (var scope = api.Services.CreateScope())
            Assert.Equal(2, await SeasonRollover.RunAsync(scope.ServiceProvider.GetRequiredService<TradeDb>(), season: 16));

        async Task<SearchPage> Search(Realm realm) =>
            (await (await buyer.PostAsJsonAsync("/listings/search", new ListingSearch(realm))).Content.ReadFromJsonAsync<SearchPage>())!;
        Assert.Empty((await Search(season)).Listings);
        Assert.Equal(listing.Id, Assert.Single((await Search(Realm.Eternal(hardcore: true))).Listings).Id);
        Assert.Equal(OfferStatus.Pending, Assert.Single((await seller.GetFromJsonAsync<OfferBox>("/offers"))!.Received).Status); // the offer stands
        var order = Assert.Single((await buyer.GetFromJsonAsync<List<Wanted>>("/wanted/mine"))!);
        Assert.Equal(Realm.Eternal(hardcore: true), order.Want.Realm);
        Assert.Single((await (await seller.PostAsJsonAsync("/wanted/match", Ring() with { Realm = Realm.Eternal(hardcore: true) })).Content.ReadFromJsonAsync<List<Wanted>>())!);
    }
}
