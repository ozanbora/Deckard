using System.Net;
using System.Net.Http.Json;
using Deckard.Core.Trade;
using static Deckard.Trade.Tests.Fixtures;

namespace Deckard.Trade.Tests;

[Collection("TradeDb")]
public class BlockTests
{
    private static async Task<int> Found(HttpClient who, ListingSearch? search = null) =>
        (await (await who.PostAsJsonAsync("/listings/search", search ?? new ListingSearch(Realm.Eternal()))).Content.ReadFromJsonAsync<SearchPage>())!.Listings.Count;

    [DatabaseFact]
    public async Task ABlockHidesBothPlayersFromEachOther()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");
        var buyer = await api.SignedIn("Buyer#5678");
        var listing = (await (await seller.PostAsJsonAsync("/listings", Ring())).Content.ReadFromJsonAsync<Listing>())!;
        var other = (await (await seller.PostAsJsonAsync("/listings", Ring(crit: 6))).Content.ReadFromJsonAsync<Listing>())!;
        await buyer.PostAsJsonAsync($"/listings/{other.Id}/offers", new NewOffer(20_000_000));
        await buyer.PostAsJsonAsync("/wanted", new NewWanted(new ListingSearch(Realm.Eternal(), "Ring"), 10_000_000));

        Assert.Equal(HttpStatusCode.NoContent, (await buyer.PostAsJsonAsync("/blocks", new BlockRequest(ListingId: listing.Id))).StatusCode);

        Assert.Equal(0, await Found(buyer));
        Assert.Equal(HttpStatusCode.NotFound, (await buyer.PostAsJsonAsync($"/listings/{listing.Id}/offers", new NewOffer(1))).StatusCode);
        Assert.Equal(OfferStatus.Withdrawn, Assert.Single((await buyer.GetFromJsonAsync<OfferBox>("/offers"))!.Sent).Status); // open offers end
        Assert.Empty((await (await seller.PostAsJsonAsync("/wanted/browse", new ListingSearch(Realm.Eternal()))).Content.ReadFromJsonAsync<WantedPage>())!.Wanted); // both ways
        Assert.Equal(1, await buyer.GetFromJsonAsync<int>("/blocks"));

        Assert.Equal(HttpStatusCode.NotFound, (await buyer.PostAsJsonAsync("/blocks", new BlockRequest(ListingId: Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await buyer.DeleteAsync("/blocks")).StatusCode);
        Assert.Equal(2, await Found(buyer));
    }

    [DatabaseFact]
    public async Task HidesSellersWithReportsOrWithoutARecord()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");
        var buyer = await api.SignedIn("Buyer#5678");
        var shopper = await api.SignedIn("Shopper#4321");
        var listing = (await (await seller.PostAsJsonAsync("/listings", Ring())).Content.ReadFromJsonAsync<Listing>())!;
        await seller.PostAsJsonAsync("/listings", Ring(crit: 6));
        var offer = (await (await buyer.PostAsJsonAsync($"/listings/{listing.Id}/offers", new NewOffer(20_000_000))).Content.ReadFromJsonAsync<Offer>())!;

        Assert.Equal(2, await Found(shopper, new ListingSearch(Realm.Eternal(), Sellers: SellerTrust.NoReports)));
        await seller.PostAsync($"/offers/{offer.Id}/accept", null);
        await buyer.PostAsJsonAsync($"/offers/{offer.Id}/report", new TradeReport("never showed up"));

        Assert.Equal(0, await Found(shopper, new ListingSearch(Realm.Eternal(), Sellers: SellerTrust.NoReports)));
        Assert.Equal(0, await Found(shopper, new ListingSearch(Realm.Eternal(), Sellers: SellerTrust.Proven))); // no record yet either
        Assert.Equal(1, await Found(shopper));
    }
}
