using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deckard.Core.Trade;
using Deckard.Trade.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Deckard.Trade.Tests.Fixtures;

namespace Deckard.Trade.Tests;

[Collection("TradeDb")]
public class AccountTests
{
    /// <summary>A player with something in every table: a listing with an offer, an offer of theirs, a buy order, a block and a report.</summary>
    private static async Task<(HttpClient Player, HttpClient Other, Listing OthersListing)> Busy(TradeApi api)
    {
        var player = await api.SignedIn("Leaving#1234");
        var other = await api.SignedIn("Staying#5678");
        var mine = (await (await player.PostAsJsonAsync("/listings", Ring(crit: 1))).Content.ReadFromJsonAsync<Listing>())!;
        var theirs = (await (await other.PostAsJsonAsync("/listings", Ring(crit: 2))).Content.ReadFromJsonAsync<Listing>())!;
        await other.PostAsJsonAsync($"/listings/{mine.Id}/offers", new NewOffer(1_000_000));
        await player.PostAsJsonAsync($"/listings/{theirs.Id}/offers", new NewOffer(1_000_000));
        await player.PostAsJsonAsync("/wanted", new NewWanted(new ListingSearch(Realm.Eternal(), "Ring"), 1_000_000));
        var third = await api.SignedIn("Third#9999");
        var thirds = (await (await third.PostAsJsonAsync("/listings", Ring(crit: 3))).Content.ReadFromJsonAsync<Listing>())!;
        await player.PostAsJsonAsync("/blocks", new BlockRequest(ListingId: thirds.Id));
        var report = new MultipartFormDataContent { { new StringContent("bug"), "kind" }, { new StringContent("Crash"), "title" }, { new StringContent("It closed."), "description" } };
        report.Add(new ByteArrayContent(ImageSanitizerTests.Png()), "screenshot", "shot.png");
        await player.PostAsync("/feedback", report);
        return (player, other, theirs);
    }

    [DatabaseFact]
    public async Task ExportsEverythingAboutThePlayer()
    {
        using var api = new TradeApi();
        var (player, _, _) = await Busy(api);

        var export = JsonDocument.Parse(await player.GetStringAsync("/account/export")).RootElement;

        Assert.Equal("Leaving#1234", export.GetProperty("account").GetProperty("battleTag").GetString());
        Assert.Equal(1, export.GetProperty("account").GetProperty("playersYouBlocked").GetInt32());
        Assert.Equal(1, export.GetProperty("listings").GetArrayLength());
        Assert.Equal(1, export.GetProperty("offersYouMade").GetArrayLength());
        Assert.Equal(1, export.GetProperty("offersOnYourListings").GetArrayLength());
        Assert.Equal(1, export.GetProperty("buyOrders").GetArrayLength());
        Assert.Equal("Crash", export.GetProperty("feedback")[0].GetProperty("title").GetString());
        Assert.DoesNotContain("Third#9999", export.GetRawText()); // who they blocked stays unsaid
    }

    [DatabaseFact]
    public async Task DeletesTheAccountAndEverythingInIt()
    {
        using var api = new TradeApi();
        var (player, other, theirs) = await Busy(api);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeDb>();
        var me = (await db.Users.SingleAsync(u => u.BattleTag == "Leaving#1234")).Id;
        var report = await db.Feedback.SingleAsync(f => f.UserId == me);

        Assert.Equal(HttpStatusCode.NoContent, (await player.DeleteAsync("/account")).StatusCode);

        Assert.False(await db.Users.AnyAsync(u => u.Id == me));
        Assert.False(await db.Listings.AnyAsync(l => l.SellerId == me));
        Assert.False(await db.Offers.AnyAsync(o => o.BuyerId == me));
        Assert.False(await db.Wanted.AnyAsync(w => w.BuyerId == me));
        Assert.False(await db.Blocks.AnyAsync(b => b.BlockerId == me || b.BlockedId == me));
        Assert.False(await db.Feedback.AnyAsync(f => f.UserId == me));
        Assert.False(Directory.Exists(Path.Combine(api.FeedbackRoot, report.Id.ToString())));
        Assert.True(await db.Listings.AnyAsync(l => l.Id == theirs.Id));          // the other player's listing stays
        Assert.Empty((await other.GetFromJsonAsync<OfferBox>("/offers"))!.Received); // without the deleted player's offer
    }

    [DatabaseFact]
    public async Task ABanOutlivesTheDeletedAccount()
    {
        using var api = new TradeApi();
        var (player, _, _) = await Busy(api);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeDb>();
        await Moderation.RunCommandAsync(db, ["--ban", "Leaving#1234"], TextWriter.Null);

        Assert.Equal(HttpStatusCode.NoContent, (await player.DeleteAsync("/account")).StatusCode);

        var kept = await db.Users.AsNoTracking().SingleAsync(u => u.BattleTag == "Leaving#1234");
        Assert.True(kept.Banned);
        Assert.False(await db.Listings.AnyAsync(l => l.SellerId == kept.Id));
        Assert.False(await db.Feedback.AnyAsync(f => f.UserId == kept.Id));
    }
}
