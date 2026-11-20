using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Deckard.Core.Trade;
using Deckard.Trade.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Deckard.Trade.Tests;

[Collection("TradeDb")]
public class PatreonTests
{
    private const string AppState = "app-state-0123456789";

    /// <summary>Links the player's Patreon through the browser's steps; returns the result the app's listener gets.</summary>
    private static async Task<string?> Link(TradeApi api, HttpClient player, string code)
    {
        var start = await player.PostAsJsonAsync("/patreon/link", new PatreonLinkRequest(50123, AppState));
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var authorize = new Uri((await start.Content.ReadFromJsonAsync<PatreonLinkUrl>())!.Url);
        Assert.StartsWith(FakePatreon.Authority + "/oauth2/authorize", authorize.AbsoluteUri);
        var state = HttpUtility.ParseQueryString(authorize.Query)["state"];

        var browser = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var callback = await browser.GetAsync($"/auth/patreon/callback?code={code}&state={state}");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var back = callback.Headers.Location!;
        Assert.Equal("http://127.0.0.1:50123/callback", back.GetLeftPart(UriPartial.Path));
        var query = HttpUtility.ParseQueryString(back.Query);
        Assert.Equal(AppState, query["state"]);
        Assert.Null(query["code"]); // nothing but the result travels to the app
        return query["result"];
    }

    private static async Task<PatreonStatus> Status(HttpClient player) => (await player.GetFromJsonAsync<PatreonStatus>("/patreon"))!;

    [DatabaseFact]
    public async Task LinksAPatronOnceAndUnlinks()
    {
        using var api = new TradeApi();
        var player = await api.SignedIn("Patron#1234");
        var other = await api.SignedIn("Other#5678");
        Assert.Equal(new PatreonStatus(false, false), await Status(player));

        Assert.Equal("linked", await Link(api, player, FakePatreon.PatronCode));
        Assert.Equal(new PatreonStatus(true, true, EarlyAccess: true), await Status(player)); // their tier gets early builds
        Assert.Equal("already-linked", await Link(api, other, FakePatreon.PatronCode)); // one Deckard account per Patreon account
        Assert.Equal(new PatreonStatus(false, false), await Status(other));
        Assert.Equal("linked", await Link(api, other, FakePatreon.FormerCode));        // linked, but not an active patron
        Assert.Equal(new PatreonStatus(true, false), await Status(other));

        Assert.Equal(HttpStatusCode.NoContent, (await player.DeleteAsync("/patreon/link")).StatusCode);
        Assert.Equal(new PatreonStatus(false, false), await Status(player));
        Assert.Equal("failed", await Link(api, player, "bad-code"));
    }

    [DatabaseFact]
    public async Task TheWebhookUpdatesPatronsOnlyWhenSigned()
    {
        using var api = new TradeApi();
        var player = await api.SignedIn("Patron#1234");
        Assert.Equal("linked", await Link(api, player, FakePatreon.PatronCode));
        var hook = api.CreateClient();

        Task<HttpResponseMessage> Send(string trigger, string status, string campaign, string secret, string tier = "1")
        {
            var body = $$"""
                { "data": { "type": "member", "id": "m1", "attributes": { "patron_status": "{{status}}" },
                  "relationships": { "user": { "data": { "type": "user", "id": "{{FakePatreon.PatronUser}}" } },
                                     "campaign": { "data": { "type": "campaign", "id": "{{campaign}}" } },
                                     "currently_entitled_tiers": { "data": [ { "type": "tier", "id": "{{tier}}" } ] } } } }
                """;
            var request = new HttpRequestMessage(HttpMethod.Post, "/patreon/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Patreon-Event", trigger);
            request.Headers.Add("X-Patreon-Signature", Convert.ToHexString(HMACMD5.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant());
            return hook.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send("members:update", "declined_patron", FakePatreon.Campaign, "wrong-secret")).StatusCode);
        Assert.True((await Status(player)).Patron);                                                             // a forged call changes nothing
        Assert.Equal(HttpStatusCode.OK, (await Send("members:update", "declined_patron", "999", FakePatreon.WebhookSecret)).StatusCode);
        Assert.True((await Status(player)).Patron);                                                             // another campaign
        Assert.Equal(HttpStatusCode.OK, (await Send("members:update", "declined_patron", FakePatreon.Campaign, FakePatreon.WebhookSecret)).StatusCode);
        Assert.False((await Status(player)).Patron);
        Assert.Equal(HttpStatusCode.OK, (await Send("members:pledge:create", "active_patron", FakePatreon.Campaign, FakePatreon.WebhookSecret)).StatusCode);
        Assert.Equal(new PatreonStatus(true, true, EarlyAccess: false), await Status(player));  // a tier without early access
        Assert.Equal(HttpStatusCode.OK, (await Send("members:update", "active_patron", FakePatreon.Campaign, FakePatreon.WebhookSecret, FakePatreon.EarlyTier)).StatusCode);
        Assert.True((await Status(player)).EarlyAccess);
        Assert.Equal(HttpStatusCode.OK, (await Send("members:delete", "active_patron", FakePatreon.Campaign, FakePatreon.WebhookSecret)).StatusCode);
        Assert.False((await Status(player)).Patron);
    }
    [DatabaseFact]
    public async Task EarlyBuildsOnlyForPatronsWithEarlyAccess()
    {
        using var api = new TradeApi();
        var patron = await api.SignedIn("Patron#1234");
        var fan = await api.SignedIn("Fan#5678");
        Assert.Equal("linked", await Link(api, patron, FakePatreon.PatronCode));
        Assert.Equal("linked", await Link(api, fan, FakePatreon.FormerCode));

        Assert.Equal(HttpStatusCode.OK, (await patron.GetAsync("/updates/early/releases.early.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fan.GetAsync("/updates/early/releases.early.json")).StatusCode);
        // Not through the general update folder either, however the path is written.
        Assert.Equal(HttpStatusCode.NotFound, (await fan.GetAsync("/updates/x/..%2Fearly%2Freleases.early.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fan.GetAsync("/updates/early%2Freleases.early.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fan.GetAsync("/updates/releases.win.json")).StatusCode);
    }

    [DatabaseFact]
    public async Task PatronsChooseTheirNameInTheCredits()
    {
        using var api = new TradeApi();
        var patron = await api.SignedIn("Patron#1234");
        var fan = await api.SignedIn("Fan#5678");
        Assert.Equal("linked", await Link(api, patron, FakePatreon.PatronCode));
        Assert.Equal("linked", await Link(api, fan, FakePatreon.FormerCode));
        async Task<List<string>> Supporters() => (await fan.GetFromJsonAsync<List<string>>("/supporters"))!;

        Assert.Equal(HttpStatusCode.Forbidden, (await fan.PutAsJsonAsync("/patreon/credit", new CreditRequest(true, "Fan"))).StatusCode);
        foreach (var bad in new[] { "", "visit evil.com", "me@mail", "a/b", new string('x', 33) })
            Assert.Equal(HttpStatusCode.BadRequest, (await patron.PutAsJsonAsync("/patreon/credit", new CreditRequest(true, bad))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await patron.PutAsJsonAsync("/patreon/credit", new CreditRequest(true, "  Tempest Bard  "))).StatusCode);
        Assert.Equal("Tempest Bard", (await Status(patron)).CreditName);
        Assert.Equal(["Tempest Bard"], await Supporters());

        Assert.Equal(HttpStatusCode.OK, (await patron.PutAsJsonAsync("/patreon/credit", new CreditRequest(false, null))).StatusCode);
        Assert.Empty(await Supporters());
        await patron.PutAsJsonAsync("/patreon/credit", new CreditRequest(true, "Tempest"));
        using (var scope = api.Services.CreateScope())
            Assert.True(await Moderation.RunCommandAsync(scope.ServiceProvider.GetRequiredService<TradeDb>(), ["--uncredit", "Patron#1234"], TextWriter.Null));
        Assert.Null((await Status(patron)).CreditName);

        await patron.PutAsJsonAsync("/patreon/credit", new CreditRequest(true, "Tempest"));
        Assert.Equal(HttpStatusCode.NoContent, (await patron.DeleteAsync("/patreon/link")).StatusCode); // unlinking drops the name
        Assert.Equal(new PatreonStatus(false, false), await Status(patron));
    }
}

/// <summary>Patreon's token and identity endpoints: one code for an active patron of the campaign, one for a former one.</summary>
internal sealed class FakePatreon : HttpMessageHandler
{
    public const string Authority = "https://patreon.test", Campaign = "555", WebhookSecret = "hook-secret";
    public const string PatronCode = "patron-code", FormerCode = "former-code", PatronUser = "9001", EarlyTier = "777";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.RequestUri!.AbsolutePath switch
        {
            "/api/oauth2/token" when await request.Content!.ReadAsStringAsync(cancellationToken) is var form
                && form.Contains("client_secret=patreon-secret") => form.Contains("code=" + PatronCode) ? """{ "access_token": "t-patron" }"""
                    : form.Contains("code=" + FormerCode) ? """{ "access_token": "t-former" }""" : null,
            "/api/oauth2/v2/identity" when request.Headers.Authorization?.Parameter is "t-patron" or "t-former" =>
                Identity(request.Headers.Authorization.Parameter == "t-patron" ? (PatronUser, "active_patron") : ("9002", "former_patron")),
            _ => null,
        };
        return body is null
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    // The user, their membership of our campaign and of another one (which must not count).
    private static string Identity((string User, string Status) who) => $$"""
        { "data": { "type": "user", "id": "{{who.User}}" },
          "included": [
            { "type": "member", "id": "a", "attributes": { "patron_status": "active_patron" }, "relationships": { "campaign": { "data": { "type": "campaign", "id": "999" } } } },
            { "type": "member", "id": "b", "attributes": { "patron_status": "{{who.Status}}" }, "relationships": {
                "campaign": { "data": { "type": "campaign", "id": "{{Campaign}}" } },
                "currently_entitled_tiers": { "data": [ { "type": "tier", "id": "{{EarlyTier}}" } ] } } },
            { "type": "campaign", "id": "{{Campaign}}" } ] }
        """;
}
