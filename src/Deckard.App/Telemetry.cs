using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Deckard.Core.Trade;

namespace Deckard.App;

/// <summary>
/// Anonymous usage and crash data, sent to the Deckard server only when the player agreed (asked once, a switch in
/// Settings; ROADMAP.md, docs/privacy-policy.md). Kept for this Windows user in %LOCALAPPDATA%\Deckard\telemetry.json:
/// the choice and a random installation id, which is forgotten when the player says no. Never screenshots, item
/// contents, BattleTags, account ids, build links or zones; events carry only counts, sizes and Deckard's own code.
/// </summary>
public static class Telemetry
{
    private const int MaxQueued = 500, MaxBatch = 100;
    private static readonly TimeSpan SendEvery = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentQueue<TelemetryEvent> Queue = new();
    private static readonly string File = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deckard", "telemetry.json");
    private static State _state = Read();
    private static Timer? _timer;

    private sealed record State(bool? Consent, Guid? InstallId);

    /// <summary>The player's choice: null until asked.</summary>
    public static bool? Consent => _state.Consent;

    /// <summary>Records the player's choice; saying no forgets the installation id and drops what's queued.</summary>
    public static void SetConsent(bool consent)
    {
        _state = consent ? new State(true, _state.InstallId ?? Guid.NewGuid()) : new State(false, null);
        if (!consent)
            Queue.Clear();
        Save();
    }

    /// <summary>Starts sending every few minutes (and the session's first event), and logs crashes as events.</summary>
    public static void Start()
    {
        ErrorLog.Logged += (where, error) => Track("crash", ("where", where), ("type", error.GetType().FullName ?? "?"), ("stack", ErrorLog.OwnFrames(error)));
        Track("session_start");
        _timer = new Timer(async _ => await SendAsync(), null, SendEvery, SendEvery);
    }

    /// <summary>Queues an event when the player agreed (any thread). Values must be counts, sizes or names Deckard chose.</summary>
    public static void Track(string name, params (string Key, object? Value)[] props)
    {
        if (_state.Consent != true)
            return;
        var values = props.Where(p => p.Value is not null)
            .ToDictionary(p => p.Key, p => Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? "");
        Queue.Enqueue(new TelemetryEvent(name, DateTimeOffset.UtcNow, values.Count > 0 ? values : null));
        while (Queue.Count > MaxQueued && Queue.TryDequeue(out _))
        {
            // Offline for long: the oldest go first.
        }
    }

    /// <summary>Sends what's queued (signed in, agreed); what fails to send is kept for the next try.</summary>
    public static async Task SendAsync()
    {
        if (_state is not { Consent: true, InstallId: { } id } || !TradeClient.Shared.SignedIn)
            return;
        while (!Queue.IsEmpty)
        {
            var events = new List<TelemetryEvent>();
            while (events.Count < MaxBatch && Queue.TryDequeue(out var e))
                events.Add(e);
            try
            {
                await TradeClient.Shared.SendTelemetryAsync(new TelemetryBatch(id, ErrorLog.Version, Environment.OSVersion.Version.ToString(),
                    Supporter.EarlyAccess ? "early" : "win", events));
            }
            catch (Exception e) when (e is TradeException or HttpRequestException or TaskCanceledException)
            {
                foreach (var kept in events)
                    Queue.Enqueue(kept); // the server's down or has no telemetry: try again later
                return;
            }
        }
    }

    private static State Read()
    {
        try
        {
            return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<State>(System.IO.File.ReadAllText(File)) ?? new(null, null) : new(null, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(null, null); // unreadable: ask again
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(_state));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write("telemetry choice", e);
        }
    }
}
