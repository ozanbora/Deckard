using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Deckard.Trade.Api;

/// <summary>
/// Sign-in with Battle.net (OAuth authorization code flow; Battle.net has no PKCE, so the code is exchanged here with
/// the client secret, never in the app). The app listens on a loopback port and opens the browser at /start;
/// Battle.net sends the browser back to /callback, which reads the player's account id and BattleTag and sends the
/// browser on to the app with a one-time code. The app trades that code for a session at /exchange, so the token
/// never appears in a URL. Configuration: Trade:BattleNet:ClientId and ClientSecret (user secrets or the host's
/// settings), optionally RedirectUri (else this server's /auth/battlenet/callback) and Authority (tests).
/// </summary>
public static class BattleNetLogin
{
    public sealed record Exchange(string Code);

    // What /start remembers until Battle.net calls back, and what /callback leaves for /exchange.
    private sealed record Pending(int Port, string AppState);
    private sealed record Handoff(Guid UserId);

    private static readonly TimeSpan LoginWindow = TimeSpan.FromMinutes(10), HandoffWindow = TimeSpan.FromMinutes(1);

    public static void MapBattleNetLogin(this WebApplication app)
    {
        // port: the app's loopback listener; state: the app's own check that the answer belongs to its request.
        app.MapGet("/auth/battlenet/start", async (int port, string state, HttpRequest request, IConfiguration config, TradeDb db) =>
        {
            if (Settings(config, request) is not { } bnet)
                return Results.Problem("Battle.net sign-in isn't configured on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);
            if (port is < 1024 or > 65535 || state.Length is < 16 or > 128)
                return Results.BadRequest("Bad port or state.");
            var serverState = Token();
            await PendingSignIns.PutAsync(db, Key(serverState), new Pending(port, state), LoginWindow);
            return Results.Redirect($"{bnet.Authority}/authorize?response_type=code&scope=openid"
                + $"&client_id={Uri.EscapeDataString(bnet.ClientId)}&redirect_uri={Uri.EscapeDataString(bnet.RedirectUri)}&state={serverState}");
        }).AllowAnonymous().RequireRateLimiting("login");

        app.MapGet("/auth/battlenet/callback", async (string? code, string? state, string? error, HttpRequest request, IConfiguration config,
            IHttpClientFactory http, TradeDb db, ILogger<Exchange> log) =>
        {
            if (Settings(config, request) is not { } bnet || state is null || await PendingSignIns.TakeAsync<Pending>(db, Key(state)) is not { } pending)
                return Results.BadRequest("This sign-in link has expired. Start again from Deckard.");
            // Only ever back to the player's own machine: the app's loopback listener, never a host from the request.
            string Back(string query) => $"http://127.0.0.1:{pending.Port}/callback?state={Uri.EscapeDataString(pending.AppState)}&{query}";

            if (error is not null || code is null)
                return Results.Redirect(Back("error=" + Uri.EscapeDataString(error == "access_denied" ? "Sign-in was cancelled." : "Battle.net refused the sign-in.")));
            BattleNetUser player;
            try
            {
                player = await ReadPlayerAsync(http.CreateClient("battlenet"), bnet, code);
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                log.LogWarning(e, "Battle.net sign-in failed");
                return Results.Redirect(Back("error=" + Uri.EscapeDataString("Battle.net didn't confirm the sign-in. Try again.")));
            }

            var user = await FindOrCreateAsync(db, player);
            if (user.Banned)
                return Results.Redirect(Back("error=" + Uri.EscapeDataString("This account can't trade.")));
            var handoff = Token();
            await PendingSignIns.PutAsync(db, Key(handoff), new Handoff(user.Id), HandoffWindow);
            return Results.Redirect(Back("code=" + handoff));
        }).AllowAnonymous().RequireRateLimiting("login");

        app.MapPost("/auth/battlenet/exchange", async (Exchange request, TradeDb db, TokenIssuer tokens) =>
        {
            if (await PendingSignIns.TakeAsync<Handoff>(db, Key(request.Code)) is not { } handoff || await db.Users.FindAsync(handoff.UserId) is not { Banned: false } user)
                return Results.BadRequest("This sign-in has expired. Start again.");
            return Results.Ok(await Sessions.StartAsync(db, tokens, user, devLogin: false));
        }).AllowAnonymous().RequireRateLimiting("login");
    }

    private sealed record Config(string Authority, string ClientId, string ClientSecret, string RedirectUri);
    private sealed record BattleNetUser(long Id, string BattleTag);

    private static Config? Settings(IConfiguration config, HttpRequest request) =>
        config["Trade:BattleNet:ClientId"] is { Length: > 0 } id && config["Trade:BattleNet:ClientSecret"] is { Length: > 0 } secret
            ? new Config(config["Trade:BattleNet:Authority"] ?? "https://oauth.battle.net", id, secret,
                config["Trade:BattleNet:RedirectUri"] ?? $"{request.Scheme}://{request.Host}/auth/battlenet/callback")
            : null;

    /// <summary>Trades the code for Battle.net's access token (client secret by basic auth) and reads the account.</summary>
    private static async Task<BattleNetUser> ReadPlayerAsync(HttpClient http, Config bnet, string code)
    {
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, $"{bnet.Authority}/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = bnet.RedirectUri, ["scope"] = "openid",
            }),
        };
        tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(bnet.ClientId)}:{Uri.EscapeDataString(bnet.ClientSecret)}")));
        using var tokenResponse = await http.SendAsync(tokenRequest);
        tokenResponse.EnsureSuccessStatusCode();
        using var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());

        using var infoRequest = new HttpRequestMessage(HttpMethod.Get, $"{bnet.Authority}/userinfo");
        infoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.RootElement.GetProperty("access_token").GetString());
        using var infoResponse = await http.SendAsync(infoRequest);
        infoResponse.EnsureSuccessStatusCode();
        using var info = JsonDocument.Parse(await infoResponse.Content.ReadAsStringAsync());
        var id = info.RootElement.TryGetProperty("id", out var number) && number.TryGetInt64(out var n) ? n
            : long.Parse(info.RootElement.GetProperty("sub").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var battleTag = (info.RootElement.TryGetProperty("battletag", out var tag) ? tag : info.RootElement.GetProperty("battle_tag")).GetString();
        return new BattleNetUser(id, battleTag is { Length: > 0 } ? battleTag : throw new KeyNotFoundException("No BattleTag in userinfo."));
    }

    /// <summary>The player's user, by account id; a renamed player keeps their user and listings under the new BattleTag.</summary>
    private static async Task<UserRow> FindOrCreateAsync(TradeDb db, BattleNetUser player)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.BattleNetId == player.Id)
            // A development sign-in user with this BattleTag becomes this account (development data only).
            ?? await db.Users.SingleOrDefaultAsync(u => u.BattleTag == player.BattleTag && u.BattleNetId == null);
        // Another account still holding this BattleTag gave it up on Battle.net (BattleTags are unique there): free it.
        if (await db.Users.SingleOrDefaultAsync(u => u.BattleTag == player.BattleTag && u.BattleNetId != null && u.BattleNetId != player.Id) is { } stale)
        {
            stale.BattleTag = $"{stale.BattleTag}~{stale.Id:N}";
            await db.SaveChangesAsync();
        }
        if (user is null)
        {
            user = new UserRow { Id = Guid.NewGuid(), BattleTag = player.BattleTag, Created = DateTimeOffset.UtcNow };
            db.Users.Add(user);
        }
        user.BattleNetId = player.Id;
        user.BattleTag = player.BattleTag;
        await db.SaveChangesAsync();
        return user;
    }

    private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string Key(string token) => "bnet:" + token;
}
