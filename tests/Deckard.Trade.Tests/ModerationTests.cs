using System.Net;
using System.Net.Http.Json;
using Deckard.Core.Trade;
using Deckard.Trade.Api;
using Microsoft.Extensions.DependencyInjection;
using static Deckard.Trade.Tests.Fixtures;

namespace Deckard.Trade.Tests;

[Collection("TradeDb")]
public class ModerationTests
{
    [DatabaseFact]
    public async Task RefusesTheSameItemListedOverAndOver()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");

        for (var i = 0; i < ListingEndpoints.MaxTwins; i++)
            Assert.Equal(HttpStatusCode.Created, (await seller.PostAsJsonAsync("/listings", Ring(price: 1_000_000 + i))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsJsonAsync("/listings", Ring(price: 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await seller.PostAsJsonAsync("/listings", Ring(crit: 6))).StatusCode); // another roll
        var runes = new NewListing(Realm.Eternal(), "Rune", "Ber", "Common", 0, false, null, [], 1_000_000, 3);
        Assert.Equal(HttpStatusCode.Created, (await seller.PostAsJsonAsync("/listings", runes)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await seller.PostAsJsonAsync("/listings", runes)).StatusCode);   // goods stack
    }

    [DatabaseFact]
    public async Task ABannedSellerCantAcceptAnOfferMadeBefore()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");
        var buyer = await api.SignedIn("Buyer#5678");
        var listing = (await (await seller.PostAsJsonAsync("/listings", Ring(crit: 5))).Content.ReadFromJsonAsync<Listing>())!;
        var offer = (await (await buyer.PostAsJsonAsync($"/listings/{listing.Id}/offers", new NewOffer(1_000_000))).Content.ReadFromJsonAsync<Offer>())!;
        using (var scope = api.Services.CreateScope())
            await Moderation.RunCommandAsync(scope.ServiceProvider.GetRequiredService<TradeDb>(), ["--ban", "Seller#1234"], TextWriter.Null);

        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsync($"/offers/{offer.Id}/accept", null)).StatusCode);
        Assert.Null((await buyer.GetFromJsonAsync<OfferBox>("/offers"))!.Sent.Single().Counterpart); // no BattleTag reached the buyer
    }

    [DatabaseFact]
    public async Task RefusesABuyOrderWithAnOverlongName()
    {
        // The request size limit is Kestrel's (Program), which the test server doesn't run.
        using var api = new TradeApi();
        var player = await api.SignedIn("Player#1234");

        Assert.Equal(HttpStatusCode.BadRequest, (await player.PostAsJsonAsync("/wanted", new NewWanted(new ListingSearch(Realm.Eternal(), "Ring", Name: new string('x', 101)), 1_000))).StatusCode);
    }

    [Fact]
    public void DevelopmentRunsOnlyOnALocalDatabase()
    {
        Assert.True(DevelopmentSchema.IsLocal("Host=localhost;Database=deckard_trade"));
        Assert.True(DevelopmentSchema.IsLocal("Host=127.0.0.1;Database=deckard_trade"));
        Assert.False(DevelopmentSchema.IsLocal("Host=db.example.com;Database=deckard_trade"));
        Assert.False(DevelopmentSchema.IsLocal("Host=localhost,db.example.com;Database=deckard_trade"));
    }

    [DatabaseFact]
    public async Task APlayerReportedByThreePartnersLeavesTheMarketUntilReviewed()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Scammer#1234");
        var shopper = await api.SignedIn("Shopper#4321");
        await seller.PostAsJsonAsync("/listings", Ring(crit: 9));
        for (var i = 0; i < Moderation.FlagReporters; i++)
        {
            var buyer = await api.SignedIn($"Buyer{i}#100{i}");
            var listing = (await (await seller.PostAsJsonAsync("/listings", Ring(crit: i + 1))).Content.ReadFromJsonAsync<Listing>())!;
            var offer = (await (await buyer.PostAsJsonAsync($"/listings/{listing.Id}/offers", new NewOffer(1_000_000))).Content.ReadFromJsonAsync<Offer>())!;
            await seller.PostAsync($"/offers/{offer.Id}/accept", null);
            await buyer.PostAsJsonAsync($"/offers/{offer.Id}/report", new TradeReport($"scam {i}"));
        }

        async Task<int> Found() => (await (await shopper.PostAsJsonAsync("/listings/search", new ListingSearch(Realm.Eternal()))).Content.ReadFromJsonAsync<SearchPage>())!.Listings.Count;
        Assert.Equal(0, await Found());

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeDb>();
        var queue = new StringWriter();
        Assert.True(await Moderation.RunCommandAsync(db, ["--reports"], queue));
        Assert.Contains("Scammer#1234", queue.ToString());
        Assert.Contains("\"scam 2\"", queue.ToString());
        Assert.True(await Moderation.RunCommandAsync(db, ["--unflag", "Scammer#1234"], TextWriter.Null));
        Assert.Equal(1, await Found());
        Assert.False(await Moderation.RunCommandAsync(db, [], TextWriter.Null));
    }
}
