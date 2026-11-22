namespace Deckard.App;

/// <summary>
/// The player's settings, edited on the Settings page and read where they apply. Each account keeps its own in
/// settings.json in its <see cref="TradeClient.AccountFolder"/>: loaded when the player signs in, saved on every change.
/// </summary>
public sealed class AppSettings
{
    public static AppSettings Shared { get; } = new();

    /// <summary>Keys that can show the item card, by Windows virtual-key code. Deckard only reads whether one is down.</summary>
    public static readonly IReadOnlyList<(string Name, int Key)> OverlayKeys =
        [("Alt", 0x12), ("Ctrl", 0x11), ("Shift", 0x10), ("` (backquote)", 0xC0), ("Mouse button 4", 0x05), ("Mouse button 5", 0x06)];

    public static readonly IReadOnlyList<string> Realms = ["Eternal", "Eternal · Hardcore", "Seasonal", "Seasonal · Hardcore"];

    public int OverlayKey { get; set; } = 0x12;

    /// <summary>False: the item card shows while the key is held. True: a press shows it, the next press hides it.</summary>
    public bool ToggleOverlay { get; set; } = true; // press the key to show the card, again to hide it

    /// <summary>How the overlays come up, for hints: "press Alt" in toggle mode, else "hold Alt".</summary>
    public string OverlayHint => $"{(ToggleOverlay ? "press" : "hold")} {OverlayKeys.FirstOrDefault(k => k.Key == OverlayKey).Name ?? "Alt"}";

    public bool HighlightSkillTree { get; set; } = true;
    public bool ShowTradeNotices { get; set; } = true;
    public int NoticeSeconds { get; set; } = 30;

    /// <summary>The Trade tab's realm, as an index into <see cref="Realms"/>.</summary>
    public int DefaultRealm { get; set; }

    /// <summary>The chosen trade realm, with this season's number for the seasonal ones.</summary>
    public Deckard.Core.Trade.Realm TradeRealm(int season) => DefaultRealm switch
    {
        1 => Deckard.Core.Trade.Realm.Eternal(hardcore: true),
        2 => Deckard.Core.Trade.Realm.Seasonal(season),
        3 => Deckard.Core.Trade.Realm.Seasonal(season, hardcore: true),
        _ => Deckard.Core.Trade.Realm.Eternal(),
    };

    /// <summary>Share the zone name shown in game with accepted trade partners, while a deal is open.</summary>
    public bool ShareLocation { get; set; }

    /// <summary>Closing the main window keeps Deckard running in the tray (Exit is in the tray icon's menu).</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>The global hotkey (<see cref="TrayIcon.HotkeyText"/>) shows or hides Deckard.</summary>
    public bool ShowHideHotkey { get; set; } = true;

    /// <summary>Let other traders see that the player is in the game (off by default: presence is personal).</summary>
    public bool ShowOnline { get; set; }

    /// <summary>Show the item card over the game. Off, Deckard still reads items (runes, worn gear, the stash), just shows no card.</summary>
    public bool ShowItemCard { get; set; } = true;

    /// <summary>Raised on the UI thread after the Settings page changes something.</summary>
    public event Action? Changed;

    /// <summary>Puts the item card back at the game window's corner.</summary>
    public event Action? CardPositionReset;

    public void NotifyChanged() => Changed?.Invoke();

    // What settings.json holds; CardPositionReset is an action, not a setting.
    // Settings added later are nullable: a file from before them reads as null and gets the default, not false.
    private sealed record Stored(int OverlayKey, bool ToggleOverlay, bool HighlightSkillTree, bool ShowTradeNotices, int NoticeSeconds,
        int DefaultRealm, bool ShareLocation, bool? CloseToTray = null, bool? ShowHideHotkey = null, bool? ShowOnline = null, bool? ShowItemCard = null);

    private static string? File => TradeClient.Shared.AccountFolder is { } folder ? Path.Combine(folder, "settings.json") : null;
    private bool _loading;
    private bool _locked; // settings.json couldn't be read: don't overwrite the player's settings with defaults

    /// <summary>Loads the signed-in account's settings now and whenever the account changes, and saves every change.</summary>
    public static void KeepPerAccount()
    {
        Shared.Load();
        TradeClient.Shared.SessionChanged += Shared.Load;
        Shared.Changed += Shared.Save;
    }

    /// <summary>The account's settings, or the defaults for a new account (or when signed out, or the file is unreadable).</summary>
    private void Load()
    {
        var defaults = new AppSettings();
        Stored? stored = null;
        _locked = false;
        try
        {
            if (File is { } file && System.IO.File.Exists(file))
                stored = System.Text.Json.JsonSerializer.Deserialize<Stored>(System.IO.File.ReadAllText(file));
        }
        catch (System.Text.Json.JsonException)
        {
            // Damaged: keep it as settings.json.bad and start from the defaults; the next change writes a good file.
            try
            {
                System.IO.File.Move(File!, File + ".bad", overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _locked = true; // can't move it either: leave it alone
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _locked = true; // e.g. locked by a backup: use the defaults this session, but leave the file alone
        }
        // A file from an older Deckard (or edited by hand) may hold values the Settings page doesn't offer.
        OverlayKey = stored is not null && OverlayKeys.Any(k => k.Key == stored.OverlayKey) ? stored.OverlayKey : defaults.OverlayKey;
        ToggleOverlay = stored?.ToggleOverlay ?? defaults.ToggleOverlay;
        HighlightSkillTree = stored?.HighlightSkillTree ?? defaults.HighlightSkillTree;
        ShowTradeNotices = stored?.ShowTradeNotices ?? defaults.ShowTradeNotices;
        NoticeSeconds = stored is { NoticeSeconds: >= 5 and <= 300 } ? stored.NoticeSeconds : defaults.NoticeSeconds;
        DefaultRealm = stored is not null && stored.DefaultRealm >= 0 && stored.DefaultRealm < Realms.Count ? stored.DefaultRealm : defaults.DefaultRealm;
        ShareLocation = stored?.ShareLocation ?? defaults.ShareLocation;
        CloseToTray = stored?.CloseToTray ?? defaults.CloseToTray;
        ShowHideHotkey = stored?.ShowHideHotkey ?? defaults.ShowHideHotkey;
        ShowOnline = stored?.ShowOnline ?? defaults.ShowOnline;
        ShowItemCard = stored?.ShowItemCard ?? defaults.ShowItemCard;
        _loading = true;
        try
        {
            NotifyChanged(); // the readers and the Settings page pick up the account's values
        }
        finally
        {
            _loading = false;
        }
    }

    private void Save()
    {
        if (_loading || _locked || File is not { } file)
            return; // loading isn't a change; signed out, there's no account to save for; a locked file stays as it was
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            System.IO.File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(
                new Stored(OverlayKey, ToggleOverlay, HighlightSkillTree, ShowTradeNotices, NoticeSeconds, DefaultRealm, ShareLocation,
                    CloseToTray, ShowHideHotkey, ShowOnline, ShowItemCard)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not saved: the setting still applies until Deckard closes.
        }
    }

    public void ResetCardPosition() => CardPositionReset?.Invoke();
}
