using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deckard.Core.Trade;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Deckard.Trade.Api;

/// <summary>
/// Links a signed-in player's Patreon account (OAuth authorization code flow, the code exchanged here with the client
/// secret, as for Battle.net) and keeps whether they are an active patron of Deckard's campaign: read at linking and
/// then kept current by Patreon's webhook. Being a patron only lifts app conveniences (ROADMAP.md); nothing in the
/// market depends on it. The app asks for the link URL with its token (the browser has none), listens on a loopback
/// port, and asks GET /patreon afterwards, so no token or code ever travels in a URL to the app.
/// Configuration: Trade:Patreon:ClientId, ClientSecret, CampaignId and WebhookSecret (user secrets or the host's
/// settings), optionally EarlyAccessTiers (comma-separated Patreon tier ids whose patrons get early builds; unset: nobody),
/// RedirectUri (else this server's /auth/patreon/callback) and Authority (tests).
/// Patrons may also be named in the app's credits (PUT /patreon/credit, GET /supporters).
/// </summary>
public static partial class PatreonLink
{
    private sealed record Pending(Guid UserId, int Port, string AppState);
    private sealed record Config(string Authority, string ClientId, string ClientSecret, string CampaignId, string? WebhookSecret, string RedirectUri,
        IReadOnlySet<string> EarlyAccessTiers);
    private sealed record Member(string PatreonUserId, bool Patron, bool EarlyAccess);

    public const int MaxCreditName = 32;
    private static readonly TimeSpan SupportersCached = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan LinkWindow = TimeSpan.FromMinutes(10);

    public static void MapPatreonLink(this WebApplication app)
    {
        app.MapPost("/patreon/link", async (PatreonLinkRequest request, ClaimsPrincipal player, HttpRequest http, IConfiguration config, TradeDb db) =>
        {
            if (Settings(config, http) is not { } patreon)
                return NotConfigured();
            if (request.Port is < 1024 or > 65535 || request.State is not { Length: >= 16 and <= 128 })
                return Results.BadRequest("Bad port or state.");
            var serverState = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            await PendingSignIns.PutAsync(db, Key(serverState), new Pending(ListingEndpoints.PlayerId(player), request.Port, request.State), LinkWindow);
            return Results.Ok(new PatreonLinkUrl($"{patreon.Authority}/oauth2/authorize?response_type=code&scope=identity"
                + $"&client_id={Uri.EscapeDataString(patreon.ClientId)}&redirect_uri={Uri.EscapeDataString(patreon.RedirectUri)}&state={serverState}"));
        }).RequireRateLimiting("login");

        app.MapGet("/auth/patreon/callback", async (string? code, string? state, string? error, HttpRequest http, IConfiguration config,
            IHttpClientFactory clients, TradeDb db, ILogger<PatreonLinkUrl> log) =>
        {
            if (Settings(config, http) is not { } patreon || state is null || await PendingSignIns.TakeAsync<Pending>(db, Key(state)) is not { } pending)
                return Results.BadRequest("This link has expired. Start again from Deckard's Settings.");
            // Only ever back to the player's own machine, with the result alone: the app reads the rest from GET /patreon.
            IResult Back(string result) => Results.Redirect($"http://127.0.0.1:{pending.Port}/callback?state={Uri.EscapeDataString(pending.AppState)}&result={result}");
            if (error is not null || code is null)
                return Back("denied");

            Member member;
            try
            {
                member = await ReadMemberAsync(clients.CreateClient("patreon"), patreon, code);
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                log.LogWarning(e, "Patreon link failed");
                return Back("failed");
            }
            // One Deckard account per Patreon account: a pledge can't lift the limits of several accounts.
            if (await db.Patrons.AnyAsync(p => p.PatreonUserId == member.PatreonUserId && p.UserId != pending.UserId))
                return Back("already-linked");
            var row = await db.Patrons.FindAsync(pending.UserId);
            if (row is null)
                db.Patrons.Add(row = new PatronRow { UserId = pending.UserId, PatreonUserId = member.PatreonUserId });
            (row.PatreonUserId, row.Patron, row.EarlyAccess, row.CheckedAt) = (member.PatreonUserId, member.Patron, member.EarlyAccess, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            return Back("linked");
        }).AllowAnonymous().RequireRateLimiting("login");

        app.MapGet("/patreon", async (ClaimsPrincipal player, TradeDb db) =>
        {
            var row = await db.Patrons.FindAsync(ListingEndpoints.PlayerId(player));
            return Results.Ok(new PatreonStatus(row is not null, row?.Patron == true, row is { Patron: true, EarlyAccess: true }, row?.CreditName));
        }).RequireRateLimiting("player");

        // A patron chooses to be named in Deckard's credits, or not. The name is shown to every signed-in player.
        app.MapPut("/patreon/credit", async (CreditRequest request, ClaimsPrincipal player, TradeDb db, IMemoryCache cache) =>
        {
            var row = await db.Patrons.FindAsync(ListingEndpoints.PlayerId(player));
            if (row is not { Patron: true })
                return Results.Problem("Only active patrons are named in the credits.", statusCode: StatusCodes.Status403Forbidden);
            var name = request.Name?.Trim();
            if (request.Show && (name is null || !CreditName().IsMatch(name) || LinkLike().IsMatch(name)))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = [$"Use 1-{MaxCreditName} letters, digits, spaces and . , ' ! ? - _, and no links."],
                });
            row.CreditName = request.Show ? name : null;
            await db.SaveChangesAsync();
            cache.Remove(SupportersKey);
            return Results.Ok(new PatreonStatus(true, true, row.EarlyAccess, row.CreditName));
        }).RequireRateLimiting("player");

        // The credits: active patrons who chose to be named. A lapsed pledge drops off (the name comes back with a new one).
        app.MapGet("/supporters", async (TradeDb db, IMemoryCache cache) =>
            Results.Ok(await cache.GetOrCreateAsync(SupportersKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = SupportersCached;
                return (await db.Patrons.Where(p => p.Patron && p.CreditName != null).Select(p => p.CreditName!).ToListAsync())
                    .Order(StringComparer.OrdinalIgnoreCase).ToList();
            }))).RequireRateLimiting("player");

        // Unlinking forgets the link, early access and the credit name.
        app.MapDelete("/patreon/link", async (ClaimsPrincipal player, TradeDb db, IMemoryCache cache) =>
        {
            var me = ListingEndpoints.PlayerId(player);
            await db.Patrons.Where(p => p.UserId == me).ExecuteDeleteAsync();
            cache.Remove(SupportersKey);
            return Results.NoContent();
        }).RequireRateLimiting("player");

        // Patreon tells us when a membership starts, changes or ends. The one endpoint without a player token: it is
        // trusted only with a valid signature of the exact body, and only for our own campaign.
        app.MapPost("/patreon/webhook", async (HttpRequest http, IConfiguration config, TradeDb db, IMemoryCache cache) =>
        {
            if (Settings(config, http) is not { WebhookSecret: { } secret } patreon)
                return NotConfigured();
            using var buffer = new MemoryStream();
            await http.Body.CopyToAsync(buffer);
            var body = buffer.ToArray();
            if (!SignatureMatches(body, http.Headers["X-Patreon-Signature"].ToString(), secret))
                return Results.Unauthorized();

            var trigger = http.Headers["X-Patreon-Event"].ToString();
            if (!trigger.StartsWith("members:", StringComparison.Ordinal))
                return Results.Ok();
            using var json = JsonDocument.Parse(body);
            var data = json.RootElement.GetProperty("data");
            var relationships = data.GetProperty("relationships");
            if (Id(relationships, "campaign") != patreon.CampaignId || Id(relationships, "user") is not { } patreonUser)
                return Results.Ok(); // another campaign's member, or no user: nothing of ours
            var patron = trigger != "members:delete"
                && data.GetProperty("attributes").TryGetProperty("patron_status", out var status) && status.GetString() == "active_patron";
            var early = patron && Ids(relationships, "currently_entitled_tiers").Any(patreon.EarlyAccessTiers.Contains);
            await db.Patrons.Where(p => p.PatreonUserId == patreonUser)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.Patron, patron).SetProperty(x => x.EarlyAccess, early).SetProperty(x => x.CheckedAt, DateTimeOffset.UtcNow));
            cache.Remove(SupportersKey);
            return Results.Ok(); // a Patreon user who never linked a Deckard account is ignored
        }).AllowAnonymous();
    }

    private static IResult NotConfigured() =>
        Results.Problem("Patreon isn't configured on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);

    private static Config? Settings(IConfiguration config, HttpRequest request) =>
        config["Trade:Patreon:ClientId"] is { Length: > 0 } id && config["Trade:Patreon:ClientSecret"] is { Length: > 0 } secret
        && config["Trade:Patreon:CampaignId"] is { Length: > 0 } campaign
            ? new Config(config["Trade:Patreon:Authority"] ?? "https://www.patreon.com", id, secret, campaign,
                config["Trade:Patreon:WebhookSecret"] is { Length: > 0 } hook ? hook : null,
                config["Trade:Patreon:RedirectUri"] ?? $"{request.Scheme}://{request.Host}/auth/patreon/callback",
                (config["Trade:Patreon:EarlyAccessTiers"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet())
            : null;

    /// <summary>Patreon signs each webhook body with the webhook's secret: lowercase hex HMAC-MD5. Compared in constant time.</summary>
    internal static bool SignatureMatches(byte[] body, string signature, string secret)
    {
        var expected = Encoding.ASCII.GetBytes(Convert.ToHexString(HMACMD5.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant()));
    }

    /// <summary>Trades the code for an access token and reads the Patreon user, their membership of our campaign and its tiers.</summary>
    private static async Task<Member> ReadMemberAsync(HttpClient http, Config patreon, string code)
    {
        using var tokenResponse = await http.PostAsync($"{patreon.Authority}/api/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = patreon.ClientId,
            ["client_secret"] = patreon.ClientSecret, ["redirect_uri"] = patreon.RedirectUri,
        }));
        tokenResponse.EnsureSuccessStatusCode();
        using var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());

        using var identity = new HttpRequestMessage(HttpMethod.Get,
            $"{patreon.Authority}/api/oauth2/v2/identity?include=memberships.campaign,memberships.currently_entitled_tiers&fields%5Bmember%5D=patron_status");
        identity.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.RootElement.GetProperty("access_token").GetString());
        using var identityResponse = await http.SendAsync(identity);
        identityResponse.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await identityResponse.Content.ReadAsStringAsync());
        var userId = json.RootElement.GetProperty("data").GetProperty("id").GetString() ?? throw new KeyNotFoundException("No Patreon user id.");
        var membership = json.RootElement.TryGetProperty("included", out var included)
            ? included.EnumerateArray().FirstOrDefault(item => item.GetProperty("type").GetString() == "member"
                && item.TryGetProperty("relationships", out var links) && Id(links, "campaign") == patreon.CampaignId)
            : default;
        if (membership.ValueKind != JsonValueKind.Object)
            return new Member(userId, false, false);
        var patron = membership.GetProperty("attributes").TryGetProperty("patron_status", out var status) && status.GetString() == "active_patron";
        var early = patron && Ids(membership.GetProperty("relationships"), "currently_entitled_tiers").Any(patreon.EarlyAccessTiers.Contains);
        return new Member(userId, patron, early);
    }

    /// <summary>A JSON:API to-many relationship's ids: relationships.{name}.data[].id.</summary>
    private static IEnumerable<string> Ids(JsonElement relationships, string name) =>
        relationships.TryGetProperty(name, out var link) && link.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? [.. data.EnumerateArray().Select(d => d.TryGetProperty("id", out var id) ? id.GetString() : null).OfType<string>()]
            : [];

    private const string SupportersKey = "patreon:supporters";

    // A name in the credits: letters (any script), digits, spaces and a little punctuation.
    [System.Text.RegularExpressions.GeneratedRegex(@"^[\p{L}\p{M}\p{N} .,'!?_\-]{1,32}$")]
    private static partial System.Text.RegularExpressions.Regex CreditName();

    // Something that reads as a web address ("example.com", "www.x"): no advertising in the credits.
    [System.Text.RegularExpressions.GeneratedRegex(@"\w\.\w{2,}|www", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex LinkLike();

    /// <summary>A JSON:API relationship's id: relationships.{name}.data.id.</summary>
    private static string? Id(JsonElement relationships, string name) =>
        relationships.TryGetProperty(name, out var link) && link.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty("id", out var id) ? id.GetString() : null;

    private static string Key(string state) => "patreon:" + state;
}
