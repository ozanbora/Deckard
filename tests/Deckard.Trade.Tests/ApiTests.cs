using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Deckard.Core.Items;
using Deckard.Core.Trade;
using Deckard.Trade.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Deckard.Trade.Tests.Fixtures;

namespace Deckard.Trade.Tests;

/// <summary>Runs only when DECKARD_TRADE_TEST_DB holds a PostgreSQL connection string (its database is recreated).</summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    public static string? ConnectionString => Environment.GetEnvironmentVariable("DECKARD_TRADE_TEST_DB");

    public DatabaseFactAttribute()
    {
        if (string.IsNullOrEmpty(ConnectionString))
            Skip = "Set DECKARD_TRADE_TEST_DB to a PostgreSQL connection string to run the API tests.";
    }
}

/// <summary>The API on a fresh test database, with the fixture catalog, its data files and a test signing key.</summary>
public sealed class TradeApi : WebApplicationFactory<Program>
{
    /// <summary>What the data endpoints serve: the fixture catalog as season 15's file, and icon 42.</summary>
    private readonly string _dataRoot = Directory.CreateTempSubdirectory("deckard-data-").FullName;

    /// <summary>Where this test server keeps feedback screenshots.</summary>
    public string FeedbackRoot => Path.Combine(_dataRoot, "feedback");

    private readonly string _environment;

    /// <param name="environment">Development maps the stand-in login; Production is how the live server runs.</param>
    public TradeApi(string environment = "Development")
    {
        _environment = environment;
        lock (FakeGitHub.Created)
            FakeGitHub.Created.Clear(); // issues filed by an earlier test
        Directory.CreateDirectory(Path.Combine(_dataRoot, "season-15"));
        File.WriteAllText(Path.Combine(_dataRoot, "season-15", AffixCatalog.FileName), Json);
        Directory.CreateDirectory(Path.Combine(_dataRoot, "icons"));
        File.WriteAllBytes(Path.Combine(_dataRoot, "icons", "42.webp"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(_dataRoot, "updates"));
        File.WriteAllText(Path.Combine(_dataRoot, "updates", "releases.win.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_dataRoot, "updates", "early"));
        File.WriteAllText(Path.Combine(_dataRoot, "updates", "early", "releases.early.json"), "{}");

        // A clean database per test; the app creates the schema again when it starts.
        using var db = new TradeDb(new DbContextOptionsBuilder<TradeDb>().UseNpgsql(DatabaseFactAttribute.ConnectionString).Options);
        db.Database.EnsureDeleted();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("ConnectionStrings:Trade", DatabaseFactAttribute.ConnectionString ?? "Host=unused");
        builder.UseSetting("Trade:SigningKey", "test-signing-key-test-signing-key-0123456789");
        builder.UseSetting("Trade:DataRoot", _dataRoot);
        builder.UseSetting("Trade:UpdatesRoot", Path.Combine(_dataRoot, "updates"));
        builder.UseSetting("Trade:BattleNet:ClientId", "test-client");
        builder.UseSetting("Trade:BattleNet:ClientSecret", "test-secret");
        builder.UseSetting("Trade:BattleNet:Authority", FakeBattleNet.Authority);
        builder.UseSetting("Trade:Patreon:ClientId", "patreon-client");
        builder.UseSetting("Trade:Patreon:ClientSecret", "patreon-secret");
        builder.UseSetting("Trade:Patreon:CampaignId", FakePatreon.Campaign);
        builder.UseSetting("Trade:Patreon:WebhookSecret", FakePatreon.WebhookSecret);
        builder.UseSetting("Trade:Patreon:Authority", FakePatreon.Authority);
        builder.UseSetting("Trade:Patreon:EarlyAccessTiers", $"999, {FakePatreon.EarlyTier}");
        builder.UseSetting("Trade:GitHub:Token", "github-token");
        builder.UseSetting("Trade:GitHub:Repository", "ozanbora/deckard");
        builder.UseSetting("Trade:GitHub:ApiUrl", FakeGitHub.Api);
        builder.UseSetting("Trade:FeedbackRoot", FeedbackRoot);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(Catalog());
            services.AddHttpClient("battlenet").ConfigurePrimaryHttpMessageHandler(() => new FakeBattleNet());
            services.AddHttpClient("patreon").ConfigurePrimaryHttpMessageHandler(() => new FakePatreon());
            services.AddHttpClient("github").ConfigurePrimaryHttpMessageHandler(() => new FakeGitHub());
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(_dataRoot)) // the factory disposes twice
            Directory.Delete(_dataRoot, recursive: true);
    }

    public async Task<HttpClient> SignedIn(string battleTag)
    {
        var client = CreateClient();
        var login = await client.PostAsJsonAsync("/auth/dev-login", new AuthEndpoints.DevLogin(battleTag));
        login.EnsureSuccessStatusCode();
        var session = await login.Content.ReadFromJsonAsync<AuthEndpoints.Session>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session!.Token);
        return client;
    }
}

// Every test drops and recreates the one test database: the classes that use it share a collection, so they run one at a time.
[Collection("TradeDb")]
public class ApiTests
{
    [DatabaseFact]
    public async Task RefusesRequestsWithoutAToken()
    {
        using var api = new TradeApi();
        var client = api.CreateClient();

        var response = await client.PostAsJsonAsync("/listings/search", new ListingSearch(Realm.Eternal()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [DatabaseFact]
    public async Task ListsAndFindsAnItemByItsAffixes()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");
        var buyer = await api.SignedIn("Buyer#5678");

        var created = await seller.PostAsJsonAsync("/listings", Ring(crit: 5.5));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        async Task<SearchPage> Search(ListingSearch search) =>
            (await (await buyer.PostAsJsonAsync("/listings/search", search)).Content.ReadFromJsonAsync<SearchPage>())!;

        var found = await Search(new ListingSearch(Realm.Eternal(), ItemType: "Ring", Affixes: [new AffixFilter(CritChance, 5), new AffixFilter(AttackSpeed)]));
        var listing = Assert.Single(found.Listings);
        Assert.Equal("", listing.Seller); // a BattleTag is only shared through an accepted offer
        Assert.Equal(5.5, listing.Affixes.Single(a => a.Id == CritChance).Value);

        Assert.Empty((await Search(new ListingSearch(Realm.Eternal(), Affixes: [new AffixFilter(CritChance, 6)]))).Listings); // rolled too low
        Assert.Empty((await Search(new ListingSearch(Realm.Eternal(hardcore: true)))).Listings);                           // other realm
        Assert.Empty((await Search(new ListingSearch(Realm.Eternal(), MaxPriceGold: 1_000_000))).Listings);                // too expensive

        await seller.PostAsJsonAsync("/listings", Ring() with { Name = "Ring of Control", AspectId = SorcererAspect });
        Assert.Equal(2, (await Search(new ListingSearch(Realm.Eternal(), Class: "Sorcerer"))).Listings.Count);
        Assert.Equal("Rapid Ring of Precision", Assert.Single((await Search(new ListingSearch(Realm.Eternal(), Class: "Rogue"))).Listings).Name);
    }

    [DatabaseFact]
    public async Task FiltersByRollRangeGreaterAffixesAndPriceAndSorts()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");
        var buyer = await api.SignedIn("Buyer#5678");
        await seller.PostAsJsonAsync("/listings", Ring(crit: 4, price: 10_000_000));
        await seller.PostAsJsonAsync("/listings", Ring(crit: 7, price: 90_000_000));
        await seller.PostAsJsonAsync("/listings", Ring(crit: 5, price: null));

        async Task<List<double>> Crits(ListingSearch search) =>
            [.. (await (await buyer.PostAsJsonAsync("/listings/search", search)).Content.ReadFromJsonAsync<SearchPage>())!
                .Listings.Select(l => l.Affixes.Single(a => a.Id == CritChance).Value)];

        Assert.Equal([4, 7, 5], await Crits(new ListingSearch(Realm.Eternal())));                                // cheapest first, best offer last
        Assert.Equal([7, 4, 5], await Crits(new ListingSearch(Realm.Eternal(), Sort: ListingSort.PriceHigh)));
        Assert.Equal([5, 7, 4], await Crits(new ListingSearch(Realm.Eternal(), Sort: ListingSort.Newest)));
        Assert.Equal([5], await Crits(new ListingSearch(Realm.Eternal(), Affixes: [new AffixFilter(CritChance, 4.5, 6)])));
        Assert.Equal([7], await Crits(new ListingSearch(Realm.Eternal(), MinPriceGold: 50_000_000)));
        Assert.Equal(3, (await Crits(new ListingSearch(Realm.Eternal(), MinGreaterAffixes: 1, Affixes: [new AffixFilter(CritChance, Greater: true)]))).Count);
        Assert.Empty(await Crits(new ListingSearch(Realm.Eternal(), MinGreaterAffixes: 5)));
        Assert.Empty(await Crits(new ListingSearch(Realm.Eternal(), Affixes: [new AffixFilter(AttackSpeed, Greater: true)])));
    }

    [DatabaseFact]
    public async Task SharesBattleTagsOnlyThroughAnAcceptedOffer()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");
        var buyer = await api.SignedIn("Buyer#5678");
        var rival = await api.SignedIn("Rival#9999");
        var listing = (await (await seller.PostAsJsonAsync("/listings", Ring(price: 25_000_000))).Content.ReadFromJsonAsync<Listing>())!;

        Task<HttpResponseMessage> Offer(HttpClient who, long gold) => who.PostAsJsonAsync($"/listings/{listing.Id}/offers", new NewOffer(gold));
        async Task<OfferBox> Box(HttpClient who) => (await who.GetFromJsonAsync<OfferBox>("/offers"))!;
        async Task<int> Found() => (await (await buyer.PostAsJsonAsync("/listings/search", new ListingSearch(Realm.Eternal()))).Content.ReadFromJsonAsync<SearchPage>())!.Listings.Count;

        Assert.Equal(HttpStatusCode.BadRequest, (await Offer(buyer, 30_000_000)).StatusCode);  // above the asking price
        Assert.Equal(HttpStatusCode.BadRequest, (await Offer(seller, 20_000_000)).StatusCode); // own listing
        var offer = (await (await Offer(buyer, 20_000_000)).Content.ReadFromJsonAsync<Offer>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await Offer(buyer, 21_000_000)).StatusCode);    // one open offer each
        var rivalOffer = (await (await Offer(rival, 22_000_000)).Content.ReadFromJsonAsync<Offer>())!;

        var received = await Box(seller);
        Assert.Equal(2, received.Received.Count);
        Assert.All(received.Received, o => Assert.Null(o.Counterpart));
        Assert.Null(Assert.Single((await Box(buyer)).Sent).Counterpart);
        Assert.Equal(HttpStatusCode.NotFound, (await buyer.PostAsync($"/offers/{offer.Id}/accept", null)).StatusCode); // only the seller

        Assert.Equal(HttpStatusCode.OK, (await seller.PostAsync($"/offers/{offer.Id}/accept", null)).StatusCode);
        Assert.Equal("Buyer#5678", (await Box(seller)).Received.Single(o => o.Id == offer.Id).Counterpart);
        var sent = Assert.Single((await Box(buyer)).Sent);
        Assert.Equal(OfferStatus.Accepted, sent.Status);
        Assert.Equal("Seller#1234", sent.Counterpart);
        Assert.Null(Assert.Single((await Box(rival)).Sent).Counterpart);                        // the rival learns nothing
        Assert.Equal(0, await Found());                                                          // reserved: out of search
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsync($"/offers/{rivalOffer.Id}/accept", null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await buyer.PostAsync($"/offers/{offer.Id}/cancel", null)).StatusCode);
        Assert.Equal(1, await Found());                                                          // back on the market

        Assert.Equal(HttpStatusCode.NoContent, (await seller.PostAsync($"/listings/{listing.Id}/sold", null)).StatusCode);
        Assert.Equal(OfferStatus.Declined, Assert.Single((await Box(rival)).Sent).Status);       // open offers lapse with the sale
    }

    [DatabaseFact]
    public async Task PriceChecksAskingAndRecentlySoldPrices()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");
        var buyer = await api.SignedIn("Buyer#5678");
        foreach (var price in new long[] { 10_000_000, 20_000_000, 30_000_000 })
            await seller.PostAsJsonAsync("/listings", Ring(crit: price / 10_000_000, price: price)); // rolls apart: a fourth twin is refused
        var sold = (await (await seller.PostAsJsonAsync("/listings", Ring(price: 50_000_000))).Content.ReadFromJsonAsync<Listing>())!;
        await seller.PostAsJsonAsync($"/listings/{sold.Id}/sold", new SoldRequest(45_000_000));

        async Task<PriceCheck> Check(ListingSearch search) =>
            (await (await buyer.PostAsJsonAsync("/listings/price-check", search)).Content.ReadFromJsonAsync<PriceCheck>())!;

        // A sale the seller just declares (45M, no accepted offer) doesn't count: anyone could set a price check that way.
        var rings = await Check(new ListingSearch(Realm.Eternal(), ItemType: "Ring", Affixes: [new AffixFilter(CritChance)]));
        Assert.Equal(new PriceCheck(3, 10_000_000, 20_000_000, 30_000_000, 0, null), rings);
        Assert.Equal(new PriceCheck(0, null, null, null, 0, null), await Check(new ListingSearch(Realm.Eternal(hardcore: true))));

        // A sale through an accepted offer counts, at that offer's price.
        var other = (await (await seller.PostAsJsonAsync("/listings", Ring(price: 40_000_000))).Content.ReadFromJsonAsync<Listing>())!;
        var offer = (await (await buyer.PostAsJsonAsync($"/listings/{other.Id}/offers", new NewOffer(35_000_000))).Content.ReadFromJsonAsync<Offer>())!;
        await seller.PostAsync($"/offers/{offer.Id}/accept", null);
        Assert.Equal(HttpStatusCode.NoContent, (await seller.PostAsJsonAsync($"/listings/{other.Id}/sold", new SoldRequest(999_999_999))).StatusCode);
        var afterSale = await Check(new ListingSearch(Realm.Eternal()));
        Assert.Equal((1, 35_000_000L), (afterSale.Sold, afterSale.SoldMedianGold!.Value));
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync($"/listings/{other.Id}/sold", new SoldRequest(-1))).StatusCode);
    }

    [DatabaseFact]
    public async Task CountersAnOfferAndTheBuyerAcceptsOrRevises()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");
        var buyer = await api.SignedIn("Buyer#5678");
        var listing = (await (await seller.PostAsJsonAsync("/listings", Ring(price: 30_000_000))).Content.ReadFromJsonAsync<Listing>())!;
        var offer = (await (await buyer.PostAsJsonAsync($"/listings/{listing.Id}/offers", new NewOffer(20_000_000))).Content.ReadFromJsonAsync<Offer>())!;

        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync($"/offers/{offer.Id}/counter", new NewOffer(15_000_000))).StatusCode); // below the offer
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync($"/offers/{offer.Id}/counter", new NewOffer(35_000_000))).StatusCode); // above asking
        var countered = (await (await seller.PostAsJsonAsync($"/offers/{offer.Id}/counter", new NewOffer(27_000_000))).Content.ReadFromJsonAsync<Offer>())!;
        Assert.Equal((OfferStatus.Countered, 27_000_000L), (countered.Status, countered.CounterGold!.Value));
        Assert.Equal(HttpStatusCode.Conflict, (await seller.PostAsync($"/offers/{offer.Id}/accept", null)).StatusCode); // the buyer's turn

        // The buyer answers with a revised offer; the seller's turn again.
        var revised = (await (await buyer.PostAsJsonAsync($"/offers/{offer.Id}/revise", new NewOffer(24_000_000))).Content.ReadFromJsonAsync<Offer>())!;
        Assert.Equal((OfferStatus.Pending, 24_000_000L, (long?)null), (revised.Status, revised.PriceGold, revised.CounterGold));

        // Countered again, and this time the buyer takes it: the deal stands at the counter price.
        await seller.PostAsJsonAsync($"/offers/{offer.Id}/counter", new NewOffer(26_000_000));
        var deal = (await (await buyer.PostAsync($"/offers/{offer.Id}/accept", null)).Content.ReadFromJsonAsync<Offer>())!;
        Assert.Equal((OfferStatus.Accepted, 26_000_000L, "Seller#1234"), (deal.Status, deal.PriceGold, deal.Counterpart));
    }

    [DatabaseFact]
    public async Task OffersLapseWhenTheirListingExpires()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");
        var buyer = await api.SignedIn("Buyer#5678");
        async Task<(Listing, Offer)> Offered(double crit)
        {
            var listing = (await (await seller.PostAsJsonAsync("/listings", Ring(crit: crit))).Content.ReadFromJsonAsync<Listing>())!;
            return (listing, (await (await buyer.PostAsJsonAsync($"/listings/{listing.Id}/offers", new NewOffer(1_000_000))).Content.ReadFromJsonAsync<Offer>())!);
        }
        var (expired, lapsed) = await Offered(1);
        var (_, open) = await Offered(2);
        var (reserved, deal) = await Offered(3);
        await seller.PostAsync($"/offers/{deal.Id}/accept", null); // an open deal outlives its listing's expiry

        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradeDb>();
            var yesterday = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);
            foreach (var id in new[] { expired.Id, reserved.Id })
                await db.Listings.Where(l => l.Id == id).ExecuteUpdateAsync(l => l.SetProperty(x => x.Expires, yesterday));
        }

        // The seller looks: the offer on the expired listing is gone from their box; the buyer finds it declined.
        var sellers = (await seller.GetFromJsonAsync<OfferBox>("/offers"))!;
        Assert.Equal(new[] { open.Id, deal.Id }.Order(), sellers.Received.Select(o => o.Id).Order());
        var buyers = (await buyer.GetFromJsonAsync<OfferBox>("/offers"))!.Sent.ToDictionary(o => o.Id, o => o.Status);
        Assert.Equal((OfferStatus.Declined, OfferStatus.Pending, OfferStatus.Accepted), (buyers[lapsed.Id], buyers[open.Id], buyers[deal.Id]));
    }

    [DatabaseFact]
    public async Task AnOfferChangedSinceItWasReadIsNotOverwritten()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");
        var buyer = await api.SignedIn("Buyer#5678");
        var listing = (await (await seller.PostAsJsonAsync("/listings", Ring())).Content.ReadFromJsonAsync<Listing>())!;
        var offer = (await (await buyer.PostAsJsonAsync($"/listings/{listing.Id}/offers", new NewOffer(20_000_000))).Content.ReadFromJsonAsync<Offer>())!;

        // One request reads the offer (as Accept would)...
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeDb>();
        var stale = await db.Offers.SingleAsync(o => o.Id == offer.Id);
        // ...another changes it meanwhile (the buyer withdraws)...
        Assert.Equal(HttpStatusCode.OK, (await buyer.DeleteAsync($"/offers/{offer.Id}")).StatusCode);
        // ...so the first one's save fails instead of undoing the withdrawal.
        stale.Status = OfferStatus.Accepted;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
    }

    [DatabaseFact]
    public async Task GivesDailyListedAndSoldPrices()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");
        var buyer = await api.SignedIn("Buyer#5678");
        await seller.PostAsJsonAsync("/listings", Ring(crit: 1, price: 10_000_000));
        await seller.PostAsJsonAsync("/listings", Ring(crit: 2, price: 30_000_000));
        var sold = (await (await seller.PostAsJsonAsync("/listings", Ring(crit: 3, price: 40_000_000))).Content.ReadFromJsonAsync<Listing>())!;
        var offer = (await (await buyer.PostAsJsonAsync($"/listings/{sold.Id}/offers", new NewOffer(35_000_000))).Content.ReadFromJsonAsync<Offer>())!;
        await seller.PostAsync($"/offers/{offer.Id}/accept", null);
        await seller.PostAsync($"/listings/{sold.Id}/sold", null);

        var history = (await (await buyer.PostAsJsonAsync("/listings/price-history", new ListingSearch(Realm.Eternal(), "Ring"))).Content.ReadFromJsonAsync<PriceHistory>())!;

        var today = Assert.Single(history.Days);
        Assert.Equal(new PricePoint(DateOnly.FromDateTime(DateTime.UtcNow), 3, 30_000_000, 1, 35_000_000), today);
        Assert.Empty((await (await buyer.PostAsJsonAsync("/listings/price-history", new ListingSearch(Realm.Eternal(), "Gloves"))).Content.ReadFromJsonAsync<PriceHistory>())!.Days);
    }

    [DatabaseFact]
    public async Task CompletedDealsAndReportsMakeUpTradingRecords()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");
        var buyer = await api.SignedIn("Buyer#5678");

        async Task<Offer> Deal()
        {
            var listing = (await (await seller.PostAsJsonAsync("/listings", Ring())).Content.ReadFromJsonAsync<Listing>())!;
            var offer = (await (await buyer.PostAsJsonAsync($"/listings/{listing.Id}/offers", new NewOffer(20_000_000))).Content.ReadFromJsonAsync<Offer>())!;
            Assert.Equal(HttpStatusCode.Conflict, (await buyer.PostAsync($"/offers/{offer.Id}/complete", null)).StatusCode); // not accepted yet
            await seller.PostAsync($"/offers/{offer.Id}/accept", null);
            return offer;
        }

        var first = await Deal();
        await buyer.PostAsync($"/offers/{first.Id}/complete", null);  // the buyer vouches for the seller
        await seller.PostAsync($"/offers/{first.Id}/complete", null); // and the seller for the buyer
        var second = await Deal();
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync($"/offers/{second.Id}/report", new TradeReport(" "))).StatusCode);
        await seller.PostAsJsonAsync($"/offers/{second.Id}/report", new TradeReport("Never showed up in game."));

        var buyerSees = (await buyer.GetFromJsonAsync<OfferBox>("/offers"))!.Sent.Single(o => o.Id == first.Id);
        Assert.Equal(new TraderRecord(1, 0), buyerSees.CounterpartRecord); // the seller: one completed, no reports
        Assert.True(buyerSees.MyDone);
        var sellerSees = (await seller.GetFromJsonAsync<OfferBox>("/offers"))!.Received.Single(o => o.Id == second.Id);
        Assert.Equal(new TraderRecord(1, 1), sellerSees.CounterpartRecord); // the buyer: one completed, one report
    }

    [DatabaseFact]
    public async Task ShowsInGameSellersWhoOptInAndZonesOnlyToAcceptedPartners()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");
        var buyer = await api.SignedIn("Buyer#5678");
        var rival = await api.SignedIn("Rival#9999");
        var listing = (await (await seller.PostAsJsonAsync("/listings", Ring())).Content.ReadFromJsonAsync<Listing>())!;

        async Task<IReadOnlyList<Listing>> InGame() =>
            (await (await buyer.PostAsJsonAsync("/listings/search", new ListingSearch(Realm.Eternal(), SellerInGame: true))).Content.ReadFromJsonAsync<SearchPage>())!.Listings;

        await seller.PostAsJsonAsync("/presence", new Presence(InGame: true, ShowOnline: false, "Kyovashad", ShareZone: true));
        Assert.Empty(await InGame()); // in game, but not showing as online
        await seller.PostAsJsonAsync("/presence", new Presence(InGame: true, ShowOnline: true, "Kyovashad", ShareZone: true));
        Assert.True(Assert.Single(await InGame()).SellerInGame);

        var offer = (await (await buyer.PostAsJsonAsync($"/listings/{listing.Id}/offers", new NewOffer(20_000_000))).Content.ReadFromJsonAsync<Offer>())!;
        await rival.PostAsJsonAsync($"/listings/{listing.Id}/offers", new NewOffer(21_000_000));
        Assert.Null((await buyer.GetFromJsonAsync<OfferBox>("/offers"))!.Sent.Single().CounterpartZone); // not accepted yet
        await seller.PostAsync($"/offers/{offer.Id}/accept", null);
        var deal = (await buyer.GetFromJsonAsync<OfferBox>("/offers"))!.Sent.Single();
        Assert.Equal(("Kyovashad", true), (deal.CounterpartZone, deal.CounterpartInGame));
        Assert.Null((await rival.GetFromJsonAsync<OfferBox>("/offers"))!.Sent.Single().CounterpartZone); // only the accepted buyer

        await seller.PostAsJsonAsync("/presence", new Presence(InGame: true, ShowOnline: true, "Kyovashad", ShareZone: false));
        Assert.Null((await buyer.GetFromJsonAsync<OfferBox>("/offers"))!.Sent.Single().CounterpartZone); // sharing turned off
    }

    [DatabaseFact]
    public async Task RenewsAListingForAnotherFortnight()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");
        var other = await api.SignedIn("Other#4321");
        var listing = (await (await seller.PostAsJsonAsync("/listings", Ring())).Content.ReadFromJsonAsync<Listing>())!;

        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/listings/{listing.Id}/renew", null)).StatusCode);
        var renewed = (await (await seller.PostAsync($"/listings/{listing.Id}/renew", null)).Content.ReadFromJsonAsync<Listing>())!;
        Assert.True(renewed.Expires >= listing.Expires);
        Assert.InRange(renewed.Expires - DateTimeOffset.UtcNow, TimeSpan.FromDays(13.9), TimeSpan.FromDays(14));
    }

    [DatabaseFact]
    public async Task ListsAndFindsGoodsByNameAndQuantity()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");
        var buyer = await api.SignedIn("Buyer#5678");
        NewListing Runes(string name, int quantity, long? price = 50_000_000) =>
            new(Realm.Eternal(), "Rune", name, "Common", 0, false, null, [], price, quantity);

        var created = await seller.PostAsJsonAsync("/listings", Runes("Ber", 5));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(5, (await created.Content.ReadFromJsonAsync<Listing>())!.Quantity);
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync("/listings", Runes("Zod", 1))).StatusCode);          // not a rune here
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync("/listings", Runes("Ber", 0))).StatusCode);          // no quantity
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync("/listings", Runes("Ber", 1, price: null))).StatusCode); // goods need a price
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync("/listings", Ring() with { Quantity = 2 })).StatusCode);   // gear comes one at a time
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync("/listings", Runes("Ber", 1) with { Rarity = "Mythic", ItemPower = 9999, Ancestral = true })).StatusCode); // goods aren't gear
        await seller.PostAsJsonAsync("/listings", Ring());

        var found = (await (await buyer.PostAsJsonAsync("/listings/search", new ListingSearch(Realm.Eternal(), "Rune", Name: "Ber"))).Content.ReadFromJsonAsync<SearchPage>())!;
        Assert.Equal(("Ber", 5), (Assert.Single(found.Listings).Name, found.Listings[0].Quantity));
    }

    [DatabaseFact]
    public async Task RejectsAnInvalidListing()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");

        var response = await seller.PostAsJsonAsync("/listings", Ring() with { ItemType = "Gloves" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [DatabaseFact]
    public async Task OnlyTheSellerClosesAListing()
    {
        using var api = new TradeApi();
        var seller = await api.SignedIn("Seller#1234");
        var other = await api.SignedIn("Other#4321");
        var listing = await (await seller.PostAsJsonAsync("/listings", Ring())).Content.ReadFromJsonAsync<Listing>();

        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/listings/{listing!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await seller.PostAsync($"/listings/{listing.Id}/sold", null)).StatusCode);
        Assert.Empty((await seller.GetFromJsonAsync<List<Listing>>("/listings/mine"))!);
    }

    [DatabaseFact]
    public async Task ServesGameDataAndIconsToSignedInPlayersOnly()
    {
        using var api = new TradeApi();
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/data/d4/affix-templates.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/icons/42.webp")).StatusCode);
        var player = await api.SignedIn("Player#1234");

        var data = await player.GetAsync("/data/d4/affix-templates.json");
        Assert.Equal(HttpStatusCode.OK, data.StatusCode);
        Assert.Equal(15, AffixCatalog.Load(await data.Content.ReadAsStreamAsync()).Season);

        // The app sends back the ETag it has; unchanged data isn't downloaded again.
        using var again = new HttpRequestMessage(HttpMethod.Get, "/data/d4/affix-templates.json");
        again.Headers.IfNoneMatch.Add(data.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await player.SendAsync(again)).StatusCode);

        var icon = await player.GetAsync("/icons/42.webp");
        Assert.Equal("image/webp", icon.Content.Headers.ContentType?.MediaType);
        Assert.Equal([1, 2, 3], await icon.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await player.GetAsync("/icons/43.webp")).StatusCode);
    }

    [DatabaseFact]
    public async Task SignsInWithBattleNetThroughTheAppsLoopback()
    {
        using var api = new TradeApi();
        var browser = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        const string appState = "app-state-0123456789";

        // The app opens /start; the server sends the browser to Battle.net with its own state.
        var start = await browser.GetAsync($"/auth/battlenet/start?port=50123&state={appState}");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = start.Headers.Location!;
        Assert.StartsWith(FakeBattleNet.Authority + "/authorize?", authorize.ToString());
        var state = System.Web.HttpUtility.ParseQueryString(authorize.Query)["state"];

        // Battle.net calls back; the server reads the account and sends the browser to the app's loopback port only.
        var callback = await browser.GetAsync($"/auth/battlenet/callback?code={FakeBattleNet.Code}&state={state}");
        var back = callback.Headers.Location!;
        Assert.Equal("http://127.0.0.1:50123/callback", back.GetLeftPart(UriPartial.Path));
        var query = System.Web.HttpUtility.ParseQueryString(back.Query);
        Assert.Equal(appState, query["state"]);

        // The app trades the one-time code for a session; the BattleTag is Battle.net's, and the code works once.
        var exchange = await browser.PostAsJsonAsync("/auth/battlenet/exchange", new BattleNetLogin.Exchange(query["code"]!));
        var session = await exchange.Content.ReadFromJsonAsync<AuthEndpoints.Session>();
        Assert.Equal(FakeBattleNet.BattleTag, session!.BattleTag);
        Assert.NotEqual(Guid.Empty, session.UserId); // the app keeps per-account settings under it
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsJsonAsync("/auth/battlenet/exchange", new BattleNetLogin.Exchange(query["code"]!))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.GetAsync($"/auth/battlenet/callback?code={FakeBattleNet.Code}&state={state}")).StatusCode);

        var player = api.CreateClient();
        player.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        Assert.Equal(HttpStatusCode.OK, (await player.GetAsync("/listings/mine")).StatusCode);

        // A player who cancels on Battle.net goes back to the app with the reason.
        var again = await browser.GetAsync($"/auth/battlenet/start?port=50123&state={appState}");
        var state2 = System.Web.HttpUtility.ParseQueryString(again.Headers.Location!.Query)["state"];
        var cancelled = await browser.GetAsync($"/auth/battlenet/callback?error=access_denied&state={state2}");
        Assert.Equal("Sign-in was cancelled.", System.Web.HttpUtility.ParseQueryString(cancelled.Headers.Location!.Query)["error"]);
    }

    [DatabaseFact]
    public async Task ServesUpdatesToSignedInPlayersFromTheUpdatesFolderOnly()
    {
        using var api = new TradeApi();
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/updates/releases.win.json")).StatusCode);
        var player = await api.SignedIn("Player#1234");

        var feed = await player.GetAsync("/updates/releases.win.json");
        Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
        Assert.Equal("application/json", feed.Content.Headers.ContentType?.MediaType);
        // Nothing outside the updates folder, however the path is written.
        Assert.Equal(HttpStatusCode.NotFound, (await player.GetAsync("/updates/..%2Fseason-15%2Faffix-templates.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await player.GetAsync("/updates/missing.nupkg")).StatusCode);
    }
}

/// <summary>Battle.net's token and userinfo endpoints for one known code and account.</summary>
internal sealed class FakeBattleNet : HttpMessageHandler
{
    public const string Authority = "https://bnet.test", Code = "good-code", BattleTag = "Hero#4242";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.RequestUri!.AbsolutePath switch
        {
            "/token" when request.Headers.Authorization?.Parameter == Convert.ToBase64String("test-client:test-secret"u8.ToArray())
                && (await request.Content!.ReadAsStringAsync(cancellationToken)).Contains("code=" + Code) => """{ "access_token": "bnet-token" }""",
            "/userinfo" when request.Headers.Authorization?.Parameter == "bnet-token" => $$"""{ "sub": "777", "id": 777, "battletag": "{{BattleTag}}" }""",
            _ => null,
        };
        return body is null
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }
}
