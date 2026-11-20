using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using Deckard.Core.Trade;
using Windows.Security.Credentials;

namespace Deckard.App;

/// <summary>
/// Talks to the Deckard server: trading, the game data and icons the app runs on, and updates. The server address comes
/// from the DECKARD_TRADE_SERVER environment variable, else %LOCALAPPDATA%\Deckard\trade-server.txt, else the address a
/// release was built with (DeckardServer), else the local development server. The session is kept in Windows'
/// Credential Locker between runs.
/// </summary>
public sealed class TradeClient
{
    public const string DevelopmentServer = "http://localhost:5180";
    private readonly HttpClient _http;
    // One saved session per server: a development server's token means nothing to the production one.
    private readonly string _vaultResource;

    /// <summary>The app's one connection: the game data, icons and the Trade tab share its sign-in.</summary>
    public static TradeClient Shared { get; } = new();

    private TradeClient()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deckard", "trade-server.txt");
        var builtIn = typeof(TradeClient).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "DeckardServer")?.Value;
        var server = Environment.GetEnvironmentVariable("DECKARD_TRADE_SERVER")
            ?? (File.Exists(file) ? File.ReadAllText(file).Trim() : builtIn ?? DevelopmentServer);
        _http = new HttpClient(new SessionCheck(this)) { BaseAddress = new Uri(server), Timeout = TimeSpan.FromSeconds(60) }; // the game data is ~6 MB
        _vaultResource = $"Deckard trade server {_http.BaseAddress.GetLeftPart(UriPartial.Authority)}";
        RestoreSession();
    }

    public string? BattleTag { get; private set; }
    public bool SignedIn => BattleTag is not null;

    public Uri ServerAddress => _http.BaseAddress!;

    /// <summary>The session's Authorization header value, for downloads made outside this client (updates).</summary>
    public string? Authorization => _http.DefaultRequestHeaders.Authorization?.ToString();

    /// <summary>The signed-in player's Deckard id (one per Battle.net account; kept when the BattleTag changes).</summary>
    public Guid? UserId { get; private set; }

    /// <summary>
    /// Where this account's own settings live (its build, quick searches, whether it has seen the guide):
    /// %LOCALAPPDATA%\Deckardccounts\&lt;user id&gt;. Null while signed out.
    /// </summary>
    public string? AccountFolder => UserId is { } id
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deckard", "accounts", id.ToString("N"))
        : null;

    /// <summary>Signed in, out, or as another player. Raised on the thread that signed in or out (the UI's).</summary>
    public event Action? SessionChanged;

    /// <summary>
    /// The server turned the session down (401): it expired, or the server's signing key changed. Raised on the thread
    /// of the call; the player still looked signed in, with every call answering "Sign in first.".
    /// </summary>
    public event Action? SessionRejected;

    private sealed class SessionCheck(TradeClient client) : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && request.Headers.Authorization is not null && request.RequestUri?.AbsolutePath != EndSessionPath)
                client.SessionRejected?.Invoke();
            return response;
        }
    }

    /// <summary>A server on this machine: the development one, which also offers <see cref="SignInAsync(string)"/>.</summary>
    public bool IsDevelopmentServer => _http.BaseAddress!.IsLoopback;

    private sealed record DevLogin(string BattleTag);
    private sealed record Session(string Token, string BattleTag, DateTimeOffset Expires, Guid UserId);
    private sealed record Exchange(string Code);

    /// <summary>The development stand-in for Battle.net sign-in (development servers only). The session is saved for the next run.</summary>
    public async Task SignInAsync(string battleTag)
    {
        var response = await _http.PostAsJsonAsync("/auth/dev-login", new DevLogin(battleTag));
        await EnsureOk(response);
        Start(await response.Content.ReadFromJsonAsync<Session>() ?? throw new TradeException("The server sent no session."));
    }

    /// <summary>
    /// Signs in with Battle.net in the player's browser: Deckard listens on a loopback port, the server runs the OAuth
    /// flow and sends the browser back here with a one-time code, which is traded for a session. The password only
    /// ever goes to Battle.net.
    /// </summary>
    public async Task SignInWithBattleNetAsync(CancellationToken ct)
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var state = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            new Uri(_http.BaseAddress!, $"/auth/battlenet/start?port={port}&state={state}").AbsoluteUri) { UseShellExecute = true });

        var query = await WaitForCallbackAsync(listener, state, q => q["code"] is not null
            ? "You're signed in to Deckard. You can close this tab."
            : $"Deckard couldn't sign you in: {q["error"]} You can close this tab.", ct);
        var code = query["code"] ?? throw new TradeException(query["error"] ?? "Battle.net didn't sign you in.");
        var response = await _http.PostAsJsonAsync("/auth/battlenet/exchange", new Exchange(code), ct);
        await EnsureOk(response);
        Start(await response.Content.ReadFromJsonAsync<Session>(ct) ?? throw new TradeException("The server sent no session."));
    }

    public async Task<PatreonStatus> PatreonStatusAsync() => await Read<PatreonStatus>(await _http.GetAsync("/patreon"));

    public async Task UnlinkPatreonAsync() => await EnsureOk(await _http.DeleteAsync("/patreon/link"));

    /// <summary>Shows this patron in Deckard's credits under <paramref name="name"/>, or (null) takes them off.</summary>
    public async Task SetCreditAsync(string? name) =>
        await EnsureOk(await _http.PutAsJsonAsync("/patreon/credit", new CreditRequest(name is not null, name)));

    /// <summary>The names of the patrons who chose to be in the credits.</summary>
    public async Task<IReadOnlyList<string>> SupportersAsync() => await Read<List<string>>(await _http.GetAsync("/supporters"));

    /// <summary>
    /// Links the signed-in player's Patreon account in their browser, the way Battle.net sign-in works: Deckard listens on
    /// a loopback port, the server runs Patreon's OAuth flow and sends the browser back here with the outcome (linked,
    /// already-linked, denied or failed), which this returns. Patreon's password only ever goes to Patreon.
    /// </summary>
    public async Task<string> LinkPatreonAsync(CancellationToken ct)
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var state = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var start = await Read<PatreonLinkUrl>(await _http.PostAsJsonAsync("/patreon/link", new PatreonLinkRequest(port, state), ct));
        // The shell opens what the server sent: only ever a web page, never a file or another app's protocol.
        if (!Uri.TryCreate(start.Url, UriKind.Absolute, out var url) || !(url.Scheme == Uri.UriSchemeHttps || url.IsLoopback && url.Scheme == Uri.UriSchemeHttp))
            throw new TradeException("The server sent a bad Patreon address.");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });

        var query = await WaitForCallbackAsync(listener, state, q => q["result"] switch
        {
            "linked" => "Your Patreon is linked to Deckard. You can close this tab.",
            "already-linked" => "This Patreon account is already linked to another Deckard account. You can close this tab.",
            "denied" => "Deckard didn't get access to your Patreon membership. You can close this tab.",
            _ => "Deckard couldn't link your Patreon. You can close this tab.",
        }, ct);
        return query["result"] ?? "failed";
    }

    /// <summary>
    /// Waits (up to 10 minutes) for the browser to come back to the loopback listener with <paramref name="state"/>,
    /// answers it with the page <paramref name="answer"/> words, and returns its query.
    /// </summary>
    private static async Task<System.Collections.Specialized.NameValueCollection> WaitForCallbackAsync(System.Net.Sockets.TcpListener listener,
        string state, Func<System.Collections.Specialized.NameValueCollection, string> answer, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        while (true)
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            // Browsers also open connections they never send on; don't wait on one of those.
            using var read = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            read.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                if (await ReadCallbackAsync(connection, state, answer, read.Token) is { } query)
                    return query;
                // else a favicon request, or anything that isn't the answer to this request
            }
            catch (Exception e) when (e is OperationCanceledException or IOException && !timeout.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>
    /// Reads one request from the browser; answers the callback with a page saying it can be closed. Returns its query,
    /// or null when the request isn't the callback for <paramref name="state"/>.
    /// </summary>
    private static async Task<System.Collections.Specialized.NameValueCollection?> ReadCallbackAsync(System.Net.Sockets.TcpClient connection,
        string state, Func<System.Collections.Specialized.NameValueCollection, string> answer, CancellationToken ct)
    {
        var stream = connection.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(ct) ?? ""; // "GET /callback?state=…&code=… HTTP/1.1"
        var target = requestLine.Split(' ') is [_, var path, ..] ? path : "/";
        var query = target.StartsWith("/callback?", StringComparison.Ordinal)
            ? System.Web.HttpUtility.ParseQueryString(target["/callback".Length..])
            : null;
        var ours = query?["state"] == state;
        var message = ours ? answer(query!) : "Not found.";
        var body = System.Text.Encoding.UTF8.GetBytes(
            "<!doctype html><meta charset=utf-8><title>Deckard</title>"
            + "<body style=\"background:#0a0908;color:#e6e1d8;font:18px Georgia,serif;display:grid;place-items:center;height:90vh\">"
            + $"<p>{WebUtility.HtmlEncode(message)}</p>");
        var head = System.Text.Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(ours ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(body, ct);
        return ours ? query : null;
    }

    /// <summary>Forgets the session here and in the Credential Locker.</summary>
    public void SignOut()
    {
        if (_http.DefaultRequestHeaders.Authorization is { } auth)
            _ = EndSessionAsync(auth);
        _http.DefaultRequestHeaders.Authorization = null;
        BattleTag = null;
        UserId = null;
        try
        {
            var vault = new PasswordVault();
            foreach (var old in Saved(vault))
                vault.Remove(old);
        }
        catch (COMException)
        {
            // Nothing saved.
        }
        SessionChanged?.Invoke();
    }

    private const string EndSessionPath = "/auth/session";

    /// <summary>Ends the session on the server too, so a copy of the token stops working. Signing out doesn't wait for it.</summary>
    private async Task EndSessionAsync(AuthenticationHeaderValue auth)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, EndSessionPath) { Headers = { Authorization = auth } };
            using var response = await _http.SendAsync(request);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Offline: the session runs out by itself.
        }
    }

    /// <summary>Uses a new session and saves it for the next run.</summary>
    private void Start(Session session)
    {
        Use(session);
        try
        {
            var vault = new PasswordVault();
            foreach (var old in Saved(vault))
                vault.Remove(old);
            vault.Add(new PasswordCredential(_vaultResource, session.BattleTag, $"{session.Expires:O}|{session.UserId}|{session.Token}"));
        }
        catch (COMException)
        {
            // Not remembering the session only means signing in again next time.
        }
        SessionChanged?.Invoke();
    }

    /// <summary>Signs in with the session an earlier run saved, unless it expires within the hour.</summary>
    private void RestoreSession()
    {
        try
        {
            // Before sessions were kept per server they shared this name; a session found there may be another server's.
            var vault = new PasswordVault();
            foreach (var legacy in vault.RetrieveAll().Where(c => c.Resource == "Deckard trade server"))
                vault.Remove(legacy);
        }
        catch (COMException)
        {
            // Nothing saved.
        }
        try
        {
            foreach (var credential in Saved(new PasswordVault()))
            {
                credential.RetrievePassword();
                // "expires|user id|token"; anything else (an older Deckard's session) means signing in again.
                var parts = credential.Password.Split('|', 3);
                if (parts.Length == 3 && DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires)
                    && expires > DateTimeOffset.UtcNow.AddHours(1) && Guid.TryParse(parts[1], out var userId))
                    Use(new Session(parts[2], credential.UserName, expires, userId));
            }
        }
        catch (COMException)
        {
            // An unreadable vault means signing in again.
        }
    }

    private void Use(Session session)
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        BattleTag = session.BattleTag;
        UserId = session.UserId;
    }

    private IReadOnlyList<PasswordCredential> Saved(PasswordVault vault)
    {
        try
        {
            return vault.FindAllByResource(_vaultResource);
        }
        catch (COMException)
        {
            return []; // nothing saved yet
        }
    }

    /// <summary>The season's item data and its ETag, or null when the copy with <paramref name="etag"/> is still current.</summary>
    public async Task<(byte[] Data, string? ETag)?> GameDataAsync(string? etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/data/d4/affix-templates.json");
        if (etag is not null && EntityTagHeaderValue.TryParse(etag, out var tag))
            request.Headers.IfNoneMatch.Add(tag);
        using var response = await _http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return null;
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new TradeException("the Deckard server has no game data (an older server, or one without this season's data).");
        await EnsureOk(response);
        return (await response.Content.ReadAsByteArrayAsync(), response.Headers.ETag?.ToString());
    }

    public async Task<byte[]> IconAsync(long id)
    {
        using var response = await _http.GetAsync($"/icons/{id}.webp");
        await EnsureOk(response);
        return await response.Content.ReadAsByteArrayAsync();
    }

    public async Task<SearchPage> SearchAsync(ListingSearch search) =>
        await Read<SearchPage>(await _http.PostAsJsonAsync("/listings/search", search));

    public async Task<IReadOnlyList<Listing>> MineAsync() =>
        await Read<List<Listing>>(await _http.GetAsync("/listings/mine"));

    public async Task<Listing> CreateAsync(NewListing listing)
    {
        var created = await Read<Listing>(await _http.PostAsJsonAsync("/listings", listing));
        Telemetry.Track("listing_created");
        return created;
    }

    /// <summary>Everything the server keeps about the player, as the server's JSON.</summary>
    public async Task<byte[]> ExportAccountAsync()
    {
        using var response = await _http.GetAsync("/account/export");
        await EnsureOk(response);
        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>Deletes the player's account and everything the server keeps about it; the session is gone after.</summary>
    public async Task DeleteAccountAsync() => await EnsureOk(await _http.DeleteAsync("/account"));

    public async Task SendTelemetryAsync(TelemetryBatch batch) => await EnsureOk(await _http.PostAsJsonAsync("/telemetry", batch));

    /// <summary>A bug report or feature request; the screenshots are PNGs already covered on this PC.</summary>
    public async Task<FeedbackReceipt> SendFeedbackAsync(string kind, string title, string description, string? diagnostics, IReadOnlyList<byte[]> screenshots)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(kind), "kind" },
            { new StringContent(title), "title" },
            { new StringContent(description), "description" },
        };
        if (diagnostics is not null)
            form.Add(new StringContent(diagnostics), "diagnostics");
        for (var i = 0; i < screenshots.Count; i++)
        {
            var image = new ByteArrayContent(screenshots[i]);
            image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(image, "screenshot", $"screenshot{i + 1}.png");
        }
        return await Read<FeedbackReceipt>(await _http.PostAsync("/feedback", form));
    }

    /// <param name="priceGold">What it went for, if not the accepted offer's or the asking price.</param>
    public async Task MarkSoldAsync(Guid id, long? priceGold = null) =>
        await EnsureOk(await _http.PostAsJsonAsync($"/listings/{id}/sold", new SoldRequest(priceGold)));

    public async Task<Listing> RenewAsync(Guid id) => await Read<Listing>(await _http.PostAsync($"/listings/{id}/renew", null));

    public async Task<PriceCheck> PriceCheckAsync(ListingSearch search) =>
        await Read<PriceCheck>(await _http.PostAsJsonAsync("/listings/price-check", search));

    public async Task<PriceHistory> PriceHistoryAsync(ListingSearch search) =>
        await Read<PriceHistory>(await _http.PostAsJsonAsync("/listings/price-history", search));

    public async Task<Offer> CounterAsync(Guid offer, long priceGold) =>
        await Read<Offer>(await _http.PostAsJsonAsync($"/offers/{offer}/counter", new NewOffer(priceGold)));

    public async Task<Offer> ReviseOfferAsync(Guid offer, long priceGold) =>
        await Read<Offer>(await _http.PostAsJsonAsync($"/offers/{offer}/revise", new NewOffer(priceGold)));

    public async Task<Offer> CompleteDealAsync(Guid offer) => await Read<Offer>(await _http.PostAsync($"/offers/{offer}/complete", null));

    public async Task<Offer> ReportDealAsync(Guid offer, string reason) =>
        await Read<Offer>(await _http.PostAsJsonAsync($"/offers/{offer}/report", new TradeReport(reason)));

    public async Task UpdatePresenceAsync(Presence presence) => await EnsureOk(await _http.PostAsJsonAsync("/presence", presence));

    public async Task WithdrawAsync(Guid id) => await EnsureOk(await _http.DeleteAsync($"/listings/{id}"));

    public async Task<Offer> MakeOfferAsync(Guid listing, long priceGold) =>
        await Read<Offer>(await _http.PostAsJsonAsync($"/listings/{listing}/offers", new NewOffer(priceGold)));

    public async Task<OfferBox> OffersAsync() => await Read<OfferBox>(await _http.GetAsync("/offers"));

    public async Task<Offer> AcceptOfferAsync(Guid offer) => await Read<Offer>(await _http.PostAsync($"/offers/{offer}/accept", null));

    public async Task<Offer> DeclineOfferAsync(Guid offer) => await Read<Offer>(await _http.PostAsync($"/offers/{offer}/decline", null));

    public async Task<Offer> WithdrawOfferAsync(Guid offer) => await Read<Offer>(await _http.DeleteAsync($"/offers/{offer}"));

    public async Task<Offer> CancelDealAsync(Guid offer) => await Read<Offer>(await _http.PostAsync($"/offers/{offer}/cancel", null));

    public async Task<Wanted> CreateWantedAsync(NewWanted wanted) => await Read<Wanted>(await _http.PostAsJsonAsync("/wanted", wanted));

    public async Task<IReadOnlyList<Wanted>> MyWantedAsync() => await Read<List<Wanted>>(await _http.GetAsync("/wanted/mine"));

    public async Task CancelWantedAsync(Guid id) => await EnsureOk(await _http.DeleteAsync($"/wanted/{id}"));

    /// <summary>Other players' open buy orders in the search's realm (and item type), best paying first.</summary>
    public async Task<WantedPage> BrowseWantedAsync(ListingSearch search) => await Read<WantedPage>(await _http.PostAsJsonAsync("/wanted/browse", search));

    /// <summary>The buy orders this item fits, best paying first.</summary>
    public async Task<IReadOnlyList<Wanted>> MatchWantedAsync(NewListing item) => await Read<List<Wanted>>(await _http.PostAsJsonAsync("/wanted/match", item));

    /// <summary>Sells the item to a buy order: the deal is accepted at once, at the order's price.</summary>
    public async Task<Offer> FillWantedAsync(Guid id, NewListing item) => await Read<Offer>(await _http.PostAsJsonAsync($"/wanted/{id}/fill", item));

    /// <summary>Blocks the player behind a listing, offer or buy order, both ways, without learning who it is.</summary>
    public async Task BlockAsync(BlockRequest request) => await EnsureOk(await _http.PostAsJsonAsync("/blocks", request));

    public async Task<int> BlockedCountAsync() => await Read<int>(await _http.GetAsync("/blocks"));

    public async Task UnblockAllAsync() => await EnsureOk(await _http.DeleteAsync("/blocks"));

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        await EnsureOk(response);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>() ?? throw new TradeException("The server sent an empty answer.");
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException)
        {
            // A proxy or captive portal answering with a web page, or a cut-off reply: an error the callers already show.
            throw new TradeException("The trade server sent something Deckard can't read.");
        }
    }

    /// <summary>Turns error responses into messages a player can act on (the server's validation messages included).</summary>
    private static async Task EnsureOk(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync();
        throw new TradeException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Sign in first.",
            HttpStatusCode.Forbidden => "This account can't trade.",
            HttpStatusCode.TooManyRequests => "Too many requests; wait a minute.",
            // 503: a feature this server doesn't run (Patreon, Battle.net sign-in), which it names.
            HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable when ValidationMessages(body) is { Length: > 0 } messages => messages,
            HttpStatusCode.NotFound => "That listing, offer or buy order is no longer there.",
            _ => $"The trade server answered {(int)response.StatusCode} {response.ReasonPhrase}.",
        });
    }

    private static string ValidationMessages(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            // A plain message (dev-login's bad BattleTag) is a JSON string. Validation problems list "errors"; other
            // problems (a 409 conflict) say it in "detail".
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.String)
                return doc.RootElement.GetString() ?? "";
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return "";
            return doc.RootElement.TryGetProperty("errors", out var errors)
                ? string.Join("\n", errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray()).Select(m => m.GetString()))
                : doc.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() ?? "" : "";
        }
        catch (System.Text.Json.JsonException)
        {
            return body.Length < 200 ? body : "";
        }
    }
}

public sealed class TradeException(string message) : Exception(message);
