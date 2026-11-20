using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Deckard.Trade.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Deckard.Trade.Tests;

[Collection("TradeDb")]
public class SessionTests
{
    [DatabaseFact]
    public async Task SigningOutEndsOnlyThatSession()
    {
        using var api = new TradeApi();
        var laptop = await api.SignedIn("Player#1234");
        var desktop = await api.SignedIn("Player#1234");
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/offers")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await laptop.DeleteAsync("/auth/session")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/offers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await desktop.GetAsync("/offers")).StatusCode);
    }

    [DatabaseFact]
    public async Task DeletingTheAccountEndsItsSessions()
    {
        using var api = new TradeApi();
        var laptop = await api.SignedIn("Player#1234");
        var desktop = await api.SignedIn("Player#1234");
        Assert.Equal(HttpStatusCode.OK, (await desktop.GetAsync("/offers")).StatusCode); // its session cached as live

        Assert.Equal(HttpStatusCode.NoContent, (await laptop.DeleteAsync("/account")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/offers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await desktop.GetAsync("/offers")).StatusCode);
    }

    [DatabaseFact]
    public async Task ATokenWithoutASessionIsRefused()
    {
        using var api = new TradeApi();
        await api.SignedIn("Player#1234");
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey("test-signing-key-test-signing-key-0123456789"u8.ToArray());
        using var scope = api.Services.CreateScope();
        var user = scope.ServiceProvider.GetRequiredService<TradeDb>().Users.Single();
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new TokenIssuer(key).Issue(user, Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/offers")).StatusCode);
    }

    [DatabaseFact]
    public async Task TheLiveServerHasNoDevelopmentSignIn()
    {
        using var api = new TradeApi("Production");
        var client = api.CreateClient();
        // Not mapped, so nothing lets it in without a token.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/dev-login", new AuthEndpoints.DevLogin("Player#1234"))).StatusCode);

        // Nor does it take a session a development server made in the same database.
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeDb>();
        var user = new UserRow { Id = Guid.NewGuid(), BattleTag = "Player#1234", Created = DateTimeOffset.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var session = await Sessions.StartAsync(db, scope.ServiceProvider.GetRequiredService<TokenIssuer>(), user, devLogin: true);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/offers")).StatusCode);
    }
}
