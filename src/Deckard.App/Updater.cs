using Velopack;
using Velopack.Sources;

namespace Deckard.App;

/// <summary>
/// Updates an installed Deckard from the Deckard server's /updates/ (releases built by tools/installer/pack.ps1): checks
/// after sign-in, downloads in the background, and applies the update when the player restarts or exits Deckard. A
/// copy run from a build folder isn't installed and never updates. Patrons with early access (<see cref="Supporter"/>)
/// update from /updates/early/, the "early" channel, which the server serves only to them.
/// </summary>
internal sealed class Updater
{
    private readonly TradeClient _client;
    private readonly Uri _server;
    private UpdateManager _manager; // the feed last checked: it applies what it downloaded
    private VelopackAsset? _ready;

    public Updater(TradeClient client)
    {
        _client = client;
        _server = client.ServerAddress;
        _manager = Feed(early: false);
    }

    // The channel is always named: a copy installed from an early release would otherwise look for "early" in the
    // normal feed. Never a downgrade (Velopack's default), so losing early access waits for the next normal release.
    private UpdateManager Feed(bool early) => new(
        new SimpleWebSource(new Uri(_server, early ? "/updates/early/" : "/updates/"), new SignedInDownloader(_client)),
        new UpdateOptions { ExplicitChannel = early ? "early" : "win" });

    /// <summary>The version downloaded and waiting to be applied, or null.</summary>
    public string? ReadyVersion => _ready?.Version.ToString();

    /// <summary>Downloads a newer release if there is one; returns its version, or null when Deckard is current (or can't tell).</summary>
    public async Task<string?> DownloadAsync()
    {
        // Updates run code: only from a server reached over https (a local one is fine for development), so nobody on
        // the network path can hand Deckard a package.
        if (!_manager.IsInstalled || !(_server.Scheme == Uri.UriSchemeHttps || _server.IsLoopback))
            return null;
        try
        {
            await Supporter.RefreshAsync(); // early access may have started or ended on Patreon
            _manager = Feed(Supporter.EarlyAccess);
            if (await _manager.CheckForUpdatesAsync() is not { } update)
                return null;
            await _manager.DownloadUpdatesAsync(update);
            _ready = update.TargetFullRelease;
            return ReadyVersion;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException)
        {
            return null; // offline or no releases on the server: try again next start
        }
    }

    public void RestartNow()
    {
        if (_ready is { } ready)
            _manager.ApplyUpdatesAndRestart(ready);
    }

    /// <summary>On exit: the update is applied once Deckard has closed, without restarting it.</summary>
    public void ApplyOnExit()
    {
        if (_ready is { } ready)
            _manager.WaitExitThenApplyUpdates(ready, silent: true, restart: false);
    }

    /// <summary>Velopack's downloader with the player's session, since every Deckard server call needs a sign-in.</summary>
    private sealed class SignedInDownloader(TradeClient client) : IFileDownloader
    {
        private readonly HttpClientFileDownloader _inner = new();

        /// <summary>The session only goes to the Deckard server itself, never to another host a feed entry names.</summary>
        private IDictionary<string, string> Headers(string url, IDictionary<string, string>? headers)
        {
            var all = new Dictionary<string, string>(headers ?? new Dictionary<string, string>());
            var server = client.ServerAddress;
            if (client.Authorization is { } authorization && Uri.TryCreate(url, UriKind.Absolute, out var target)
                && target.Scheme == server.Scheme && string.Equals(target.Host, server.Host, StringComparison.OrdinalIgnoreCase) && target.Port == server.Port)
                all["Authorization"] = authorization;
            return all;
        }

        public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default) =>
            _inner.DownloadFile(url, targetFile, progress, Headers(url, headers), timeout, cancelToken);

        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
            _inner.DownloadBytes(url, Headers(url, headers), timeout);

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
            _inner.DownloadString(url, Headers(url, headers), timeout);
    }
}
