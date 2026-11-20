using System.Net;
using System.Net.Http.Json;
using Deckard.Core.Trade;
using Deckard.Trade.Api;
using static Deckard.Trade.Tests.Fixtures;

namespace Deckard.Trade.Tests;

[Collection("TradeDb")]
public class WantedTests
{
    private static readonly ListingSearch CritRings = new(Realm.Eternal(), "Ring", Affixes: [new AffixFilter(CritChance, 5)]);

    [Fact]
    public void AnItemFitsABuyOrderLikeASearch()
    {
        var rules = new ListingRules(Catalog());

        Assert.True(WantedEndpoints.Fits(CritRings, Ring(crit: 5.5), rules));
        Assert.False(WantedEndpoints.Fits(CritRings, Ring(crit: 4), rules));                                            // rolled too low
        Assert.False(WantedEndpoints.Fits(CritRings with { Realm = Realm.Eternal(hardcore: true) }, Ring(), rules));     // other realm
        Assert.False(WantedEndpoints.Fits(CritRings with { ItemType = "Gloves" }, Ring(), rules));
        Assert.True(WantedEndpoints.Fits(CritRings with { Affixes = [new AffixFilter(CritChance, Greater: true)] }, Ring(), rules));
        Assert.False(WantedEndpoints.Fits(CritRings with { Affixes = [new AffixFilter(AttackSpeed, Greater: true)] }, Ring(), rules));
        Assert.False(WantedEndpoints.Fits(CritRings with { AspectId = SorcererAspect }, Ring(), rules));
        Assert.True(WantedEndpoints.Fits(CritRings with { Class = "Rogue", MinItemPower = 800, MinGreaterAffixes = 1 }, Ring(), rules));
        Assert.False(WantedEndpoints.Fits(CritRings with { Class = "Rogue" }, Ring() with { AspectId = SorcererAspect }, rules));
    }

    [DatabaseFact]
    public async Task SellingToABuyOrderMakesAnAcceptedDeal()
    {
        using var api = new TradeApi();
        var buyer = await api.SignedIn("Buyer#5678");
        var seller = await api.SignedIn("Seller#1234");
        var rival = await api.SignedIn("Rival#9999");

        Assert.Equal(HttpStatusCode.BadRequest, (await buyer.PostAsJsonAsync("/wanted", new NewWanted(new ListingSearch(Realm.Eternal()), 1))).StatusCode); // wants anything
        var order = (await (await buyer.PostAsJsonAsync("/wanted", new NewWanted(CritRings, 30_000_000))).Content.ReadFromJsonAsync<Wanted>())!;
        Assert.True(order.Mine);

        async Task<List<Wanted>> Match(HttpClient who, NewListing item) =>
            (await (await who.PostAsJsonAsync("/wanted/match", item)).Content.ReadFromJsonAsync<List<Wanted>>())!;
        Assert.Equal(30_000_000, Assert.Single(await Match(seller, Ring(crit: 6))).PriceGold);
        Assert.Empty(await Match(seller, Ring(crit: 4)));
        Assert.Empty(await Match(buyer, Ring(crit: 6)));                                                       // not their own
        var browsed = (await (await seller.PostAsJsonAsync("/wanted/browse", new ListingSearch(Realm.Eternal(), "Ring"))).Content.ReadFromJsonAsync<WantedPage>())!;
        Assert.False(Assert.Single(browsed.Wanted).Mine);

        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync($"/wanted/{order.Id}/fill", Ring(crit: 4))).StatusCode);
        var deal = (await (await seller.PostAsJsonAsync($"/wanted/{order.Id}/fill", Ring(crit: 6, price: 1))).Content.ReadFromJsonAsync<Offer>())!;
        Assert.Equal(OfferStatus.Accepted, deal.Status);
        Assert.Equal(30_000_000, deal.PriceGold);
        Assert.Equal(30_000_000, deal.Listing.PriceGold);   // at the order's price, whatever the seller sent
        Assert.Equal("Buyer#5678", deal.Counterpart);
        var sent = Assert.Single((await buyer.GetFromJsonAsync<OfferBox>("/offers"))!.Sent);
        Assert.Equal("Seller#1234", sent.Counterpart);

        Assert.Equal(HttpStatusCode.NotFound, (await rival.PostAsJsonAsync($"/wanted/{order.Id}/fill", Ring(crit: 6))).StatusCode); // filled
        Assert.Equal(WantedStatus.Filled, Assert.Single((await buyer.GetFromJsonAsync<List<Wanted>>("/wanted/mine"))!).Status);
        Assert.Empty((await (await rival.PostAsJsonAsync("/listings/search", new ListingSearch(Realm.Eternal()))).Content.ReadFromJsonAsync<SearchPage>())!.Listings); // reserved
    }

    [DatabaseFact]
    public async Task OnlyTheBuyerCancelsABuyOrder()
    {
        using var api = new TradeApi();
        var buyer = await api.SignedIn("Buyer#5678");
        var seller = await api.SignedIn("Seller#1234");
        var order = (await (await buyer.PostAsJsonAsync("/wanted", new NewWanted(CritRings, 30_000_000))).Content.ReadFromJsonAsync<Wanted>())!;

        Assert.Equal(HttpStatusCode.NotFound, (await seller.DeleteAsync($"/wanted/{order.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await buyer.DeleteAsync($"/wanted/{order.Id}")).StatusCode);
        Assert.Empty(await (await seller.PostAsJsonAsync("/wanted/match", Ring(crit: 6))).Content.ReadFromJsonAsync<List<Wanted>>() ?? []);
    }
}
