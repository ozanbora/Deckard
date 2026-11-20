using System.Net;
using System.Net.Http.Json;
using Deckard.Trade.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Deckard.Trade.Tests;

/// <summary>Several server instances on one database: what one starts another can finish, and jobs run once.</summary>
[Collection("TradeDb")]
public class SingleInstanceTests
{
    [DatabaseFact]
    public async Task ASignInStartedOnOneInstanceFinishesOnAnother()
    {
        using var api = new TradeApi();
        // Another instance: its own host and memory, the same database.
        using var other = api.WithWebHostBuilder(_ => { });
        var first = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var second = other.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var start = await first.GetAsync("/auth/battlenet/start?port=50123&state=app-state-0123456789");
        var state = System.Web.HttpUtility.ParseQueryString(start.Headers.Location!.Query)["state"];
        var callback = await second.GetAsync($"/auth/battlenet/callback?code={FakeBattleNet.Code}&state={state}");
        var code = System.Web.HttpUtility.ParseQueryString(callback.Headers.Location!.Query)["code"];
        Assert.NotNull(code);

        var exchange = await first.PostAsJsonAsync("/auth/battlenet/exchange", new BattleNetLogin.Exchange(code));
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await second.PostAsJsonAsync("/auth/battlenet/exchange", new BattleNetLogin.Exchange(code))).StatusCode);
    }

    [DatabaseFact]
    public async Task AJobRunningOnOneInstanceIsSkippedOnAnother()
    {
        using var api = new TradeApi();
        using var scope = api.Services.CreateScope(); // the server is up: the schema is there
        await using var one = new TradeDb(new DbContextOptionsBuilder<TradeDb>().UseNpgsql(DatabaseFactAttribute.ConnectionString).Options);
        await using var two = new TradeDb(new DbContextOptionsBuilder<TradeDb>().UseNpgsql(DatabaseFactAttribute.ConnectionString).Options);

        bool? inner = null;
        var ran = await SingleInstance.TryRunAsync(one, SingleInstance.Feedback, async () =>
            inner = await SingleInstance.TryRunAsync(two, SingleInstance.Feedback, () => Task.CompletedTask));

        Assert.True(ran);
        Assert.False(inner); // held by the first
        Assert.True(await SingleInstance.TryRunAsync(two, SingleInstance.Feedback, () => Task.CompletedTask)); // and let go after
    }
}
