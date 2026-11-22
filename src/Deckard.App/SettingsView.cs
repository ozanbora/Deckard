using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deckard.App;

/// <summary>
/// The Settings page: the overlay's key and mode, the skill tree highlight, trade notices, location sharing, and the
/// Patreon link.
/// </summary>
public sealed partial class SettingsView : UserControl
{
    private readonly AppSettings _settings = AppSettings.Shared;
    private readonly Action _refresh;

    /// <summary>The account was deleted on the server: sign out (and <c>true</c>: delete its folder on this PC too).</summary>
    public event Action<bool>? AccountDeleted;

    /// <summary>The main window's handle, for the save dialog.</summary>
    public nint WindowHandle { get; set; }
    private bool _refreshing; // set while the controls are updated from the settings, so they don't write back

    public SettingsView()
    {
        var card = Switch("Show the item card over the game", on => _settings.ShowItemCard = on);
        var key = new ComboBox { Header = "Key", MinWidth = 220 };
        foreach (var (name, _) in AppSettings.OverlayKeys)
            key.Items.Add(name);
        key.SelectionChanged += (_, _) => Change(() => _settings.OverlayKey = AppSettings.OverlayKeys[key.SelectedIndex].Key);

        var mode = new RadioButtons { Header = "Show the item card", Items = { "While the key is held", "Press the key to show it, press again to hide it" } };
        mode.SelectionChanged += (_, _) => Change(() => _settings.ToggleOverlay = mode.SelectedIndex == 1);

        var tree = Switch("Highlight the build on the in-game skill tree and paragon boards", on => _settings.HighlightSkillTree = on);
        var reset = new Button { Content = "Reset item card position" };
        reset.Click += (_, _) => _settings.ResetCardPosition();

        var notices = Switch("Show trade notices over the game", on => _settings.ShowTradeNotices = on);
        var duration = new ComboBox { Header = "Notice duration", Items = { "15 seconds", "30 seconds", "60 seconds" }, MinWidth = 220 };
        int[] seconds = [15, 30, 60];
        duration.SelectionChanged += (_, _) => Change(() => _settings.NoticeSeconds = seconds[duration.SelectedIndex]);

        var tray = Switch("Keep Deckard in the tray when its window closes", on => _settings.CloseToTray = on);
        var telemetry = new ToggleSwitch { Header = "Send anonymous usage and crash data" };
        telemetry.Toggled += (_, _) =>
        {
            if (!_refreshing)
                Telemetry.SetConsent(telemetry.IsOn);
        };
        var hotkey = Switch($"Show or hide Deckard with {TrayIcon.HotkeyText}", on => _settings.ShowHideHotkey = on);
        var startup = new ToggleSwitch { Header = "Start Deckard with Windows, in the tray" };
        startup.Toggled += (_, _) =>
        {
            if (_refreshing)
                return;
            try
            {
                StartWithWindows.IsOn = startup.IsOn; // the Windows user's choice, not the account's: the registry, not settings.json
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                startup.IsOn = !startup.IsOn;
            }
        };

        // Blocks are made from listings, offers and buy orders on the Trade tab; here they can only be lifted, all at once
        // (who was blocked is never shown: some were only ever a listing).
        var blocked = new TextBlock { Opacity = 0.7, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        var unblock = new Button { Content = "Unblock everyone", IsEnabled = false };
        async Task ShowBlocked(Func<Task>? first = null)
        {
            try
            {
                if (first is not null)
                    await first();
                var count = TradeClient.Shared.SignedIn ? await TradeClient.Shared.BlockedCountAsync() : 0;
                blocked.Text = count == 0 ? "You haven't blocked anyone." : $"You've blocked {count} player{(count == 1 ? "" : "s")}: you and they don't see each other's listings, buy orders or offers.";
                unblock.IsEnabled = count > 0;
            }
            catch (Exception e) when (e is TradeException or System.Net.Http.HttpRequestException or TaskCanceledException)
            {
                blocked.Text = "Can't reach the trade server to show who you've blocked.";
            }
        }
        unblock.Click += async (_, _) => await ShowBlocked(TradeClient.Shared.UnblockAllAsync);
        Loaded += async (_, _) => await ShowBlocked();
        var online = Switch("Show me as in game to other traders", on => _settings.ShowOnline = on);
        var location = Switch("Share my location with accepted trade partners", on => _settings.ShareLocation = on);

        // Patreon: linking lifts the supporter limits (Supporter); the browser does the signing in, as for Battle.net.
        var patreon = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var link = new Button { Content = "Link Patreon" };
        var unlink = new Button { Content = "Unlink Patreon" };
        CancellationTokenSource? linking = null;
        void ShowPatreon(string? message = null)
        {
            patreon.Text = message ?? (Supporter.IsPatron ? "Thank you for supporting Deckard! Your supporter perks are on."
                : Supporter.Linked ? "Your Patreon is linked, but it has no active membership of Deckard's page."
                : $"Patrons of Deckard's Patreon page keep {Supporter.PatronQuickSearches} quick searches instead of {Supporter.FreeQuickSearches}, "
                    + $"any number of builds instead of {Supporter.FreeBuilds}, and sort their stash against every build. Link the Patreon account you support with.");
            link.Content = linking is null ? "Link Patreon" : "Cancel";
            link.Visibility = Supporter.Linked && linking is null ? Visibility.Collapsed : Visibility.Visible;
            unlink.Visibility = Supporter.Linked && linking is null ? Visibility.Visible : Visibility.Collapsed;
        }
        link.Click += async (_, _) =>
        {
            if (linking is not null)
            {
                linking.Cancel();
                return;
            }
            using var cts = linking = new CancellationTokenSource();
            ShowPatreon("Waiting for Patreon in your browser…");
            string? message = null;
            try
            {
                message = await TradeClient.Shared.LinkPatreonAsync(cts.Token) switch
                {
                    "linked" => null,
                    "already-linked" => "That Patreon account is already linked to another Deckard account. Unlink it there first.",
                    "denied" => "Patreon didn't give Deckard access, so nothing was linked.",
                    _ => "Deckard couldn't link your Patreon. Try again in a moment.",
                };
            }
            catch (OperationCanceledException)
            {
                message = cts.IsCancellationRequested ? null : "Patreon didn't answer in time, so nothing was linked.";
            }
            catch (Exception e) when (e is TradeException or System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException)
            {
                message = $"Couldn't link your Patreon: {e.Message}";
            }
            linking = null;
            await Supporter.RefreshAsync();
            ShowPatreon(message);
        };
        unlink.Click += async (_, _) =>
        {
            try
            {
                await TradeClient.Shared.UnlinkPatreonAsync();
                await Supporter.RefreshAsync();
                ShowPatreon();
            }
            catch (Exception e) when (e is TradeException or System.Net.Http.HttpRequestException or TaskCanceledException)
            {
                ShowPatreon($"Couldn't unlink your Patreon: {e.Message}");
            }
        };
        // Patrons only: a name in Deckard's credits, which every player sees (opt-in), and whether updates come early.
        var credits = new StackPanel { Spacing = 8 };
        var creditName = new TextBox { Header = "Your name in Deckard's credits", MaxLength = 32, MinWidth = 260, PlaceholderText = "Shown to every Deckard player" };
        var showName = new Button { Content = "Show my name" };
        var hideName = new Button { Content = "Remove my name" };
        var creditNote = new TextBlock { Opacity = 0.7, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        var early = new TextBlock { TextWrapping = TextWrapping.Wrap };
        credits.Children.Add(early);
        credits.Children.Add(creditName);
        credits.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { showName, hideName } });
        credits.Children.Add(creditNote);
        void ShowCredits(string? message = null)
        {
            credits.Visibility = Supporter.IsPatron ? Visibility.Visible : Visibility.Collapsed;
            early.Text = Supporter.EarlyAccess ? "Early access is on: new versions of Deckard come to you before everyone else."
                : "Champion patrons get new versions of Deckard before everyone else.";
            if (message is null && creditName.FocusState == FocusState.Unfocused)
                creditName.Text = Supporter.CreditName ?? TradeClient.Shared.BattleTag?.Split('#')[0] ?? "";
            showName.Content = Supporter.CreditName is null ? "Show my name" : "Change my name";
            hideName.Visibility = Supporter.CreditName is null ? Visibility.Collapsed : Visibility.Visible;
            creditNote.Text = message ?? (Supporter.CreditName is { } shown
                ? $"Every Deckard player sees \"{shown}\" in About and credits. Thank you!"
                : "Only if you want: your name then appears in About and credits for every Deckard player.");
        }
        async Task SetCredit(string? name)
        {
            try
            {
                await TradeClient.Shared.SetCreditAsync(name);
                await Supporter.RefreshAsync();
                ShowCredits();
            }
            catch (Exception e) when (e is TradeException or System.Net.Http.HttpRequestException or TaskCanceledException)
            {
                ShowCredits(e.Message);
            }
        }
        showName.Click += async (_, _) => await SetCredit(creditName.Text.Trim());
        hideName.Click += async (_, _) => await SetCredit(null);

        Supporter.Changed += () =>
        {
            ShowPatreon();
            ShowCredits();
        };
        Loaded += async (_, _) => await Supporter.RefreshAsync(); // e.g. a membership that changed on Patreon's site
        ShowPatreon();
        ShowCredits();

        // The player's own data on the server (docs/privacy-policy.md): a copy, or deleting it all.
        var download = new Button { Content = "Download my data" };
        var delete = new Button { Content = "Delete my account" };
        var accountNote = new TextBlock { Opacity = 0.7, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        download.Click += async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "deckard-account", FileTypeChoices = { ["JSON"] = [".json"] } };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            if (await picker.PickSaveFileAsync() is not { } file)
                return;
            try
            {
                await File.WriteAllBytesAsync(file.Path, await TradeClient.Shared.ExportAccountAsync());
                accountNote.Text = $"Saved to {file.Path}.";
            }
            catch (Exception e) when (e is TradeException or System.Net.Http.HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
            {
                accountNote.Text = $"Couldn't download your data: {e.Message}";
            }
        };
        delete.Click += async (_, _) =>
        {
            var local = new CheckBox { Content = "Also delete this account's builds and settings on this PC", IsChecked = true };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Delete your Deckard account?",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Text = "This deletes everything Deckard's server keeps about you, at once and for good: your listings, offers and "
                                + "buy orders, your trading record, your blocks, your sign-ins, your Patreon link and credit name, and your bug "
                                + "reports with their screenshots. Open deals end. Your Battle.net account and your game aren't touched; you can sign in "
                                + "again later as a new Deckard player.",
                        },
                        local,
                    },
                },
                PrimaryButtonText = "Delete my account",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
            try
            {
                await TradeClient.Shared.DeleteAccountAsync();
                AccountDeleted?.Invoke(local.IsChecked == true);
            }
            catch (Exception e) when (e is TradeException or System.Net.Http.HttpRequestException or TaskCanceledException)
            {
                accountNote.Text = $"Couldn't delete your account: {e.Message}";
            }
        };

        var page = new StackPanel { Spacing = 14, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(24, 16, 24, 24) };
        page.Children.Add(Heading("Overlay"));
        page.Children.Add(card);
        page.Children.Add(key);
        page.Children.Add(mode);
        page.Children.Add(Note("Deckard only reads whether the key is down; it never sends keys to the game. Alt is also the game's own key for advanced tooltips."));
        page.Children.Add(tree);
        page.Children.Add(reset);
        page.Children.Add(Heading("Trade"));
        page.Children.Add(notices);
        page.Children.Add(duration);
        page.Children.Add(online);
        page.Children.Add(Note("Others then see \"in game\" on your listings and offers, and can find sellers who are playing now. Only whether you're in game is shared, not where."));
        page.Children.Add(blocked);
        page.Children.Add(unblock);
        page.Children.Add(Heading("Location"));
        page.Children.Add(location);
        page.Children.Add(Note("When on, Deckard reads the zone name the game shows above the minimap (only from the screen, every few seconds) and shares only that name, only with the player whose offer you accepted, and only while that deal is open and you are in game. Deckard's server keeps only your latest zone, for 10 minutes, to pass it on. Turning it off stops sharing and clears it at once."));
        page.Children.Add(Heading("Patreon"));
        page.Children.Add(patreon);
        page.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { link, unlink } });
        page.Children.Add(credits);
        page.Children.Add(Note("Supporting Deckard lifts limits on conveniences only. Trading, alerts and notices work the same for everyone."));
        page.Children.Add(Heading("Window"));
        page.Children.Add(tray);
        page.Children.Add(hotkey);
        page.Children.Add(Note($"While on, {TrayIcon.HotkeyText} goes to Deckard instead of the app in front, the game included. Deckard only listens for it; it never sends keys to the game."));
        page.Children.Add(startup);
        page.Children.Add(Heading("Account"));
        page.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { download, delete } });
        page.Children.Add(Note("Download a copy of everything Deckard's server keeps about you, or delete it all."));
        page.Children.Add(accountNote);
        page.Children.Add(Heading("Privacy"));
        page.Children.Add(telemetry);
        page.Children.Add(Note("Counts and sizes only: the pages you open, how well items are read at your screen size, and where Deckard "
            + "crashed. Never screenshots, items, builds, your BattleTag or anything about the game. Turning it off also forgets "
            + "this PC's random id. For this Windows user."));
        page.Children.Add(Note("Settings are kept for your Battle.net account; starting with Windows and usage data are for this Windows user."));
        Content = new ScrollViewer { Content = page };

        // Show the current values now, and again whenever they change elsewhere (e.g. an account's settings load).
        _refresh = () =>
        {
            _refreshing = true;
            key.SelectedIndex = Math.Max(0, AppSettings.OverlayKeys.ToList().FindIndex(k => k.Key == _settings.OverlayKey));
            mode.SelectedIndex = _settings.ToggleOverlay ? 1 : 0;
            card.IsOn = _settings.ShowItemCard;
            key.IsEnabled = mode.IsEnabled = reset.IsEnabled = _settings.ShowItemCard; // the key only shows the card
            tree.IsOn = _settings.HighlightSkillTree;
            notices.IsOn = _settings.ShowTradeNotices;
            duration.SelectedIndex = Math.Max(0, Array.IndexOf(seconds, _settings.NoticeSeconds));
            location.IsOn = _settings.ShareLocation;
            tray.IsOn = _settings.CloseToTray;
            hotkey.IsOn = _settings.ShowHideHotkey;
            startup.IsOn = StartWithWindows.IsOn;
            telemetry.IsOn = Telemetry.Consent == true;
            online.IsOn = _settings.ShowOnline;
            _refreshing = false;
        };
        _refresh();
        _settings.Changed += () => DispatcherQueue.TryEnqueue(() => _refresh());
        Loaded += (_, _) => _refresh(); // e.g. the usage data choice, made in the first-start question
    }

    private void Change(Action set)
    {
        if (_refreshing)
            return;
        set();
        _settings.NotifyChanged();
    }

    private ToggleSwitch Switch(string header, Action<bool> set)
    {
        var toggle = new ToggleSwitch { Header = header };
        toggle.Toggled += (_, _) => Change(() => set(toggle.IsOn));
        return toggle;
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
        Margin = new Thickness(0, 10, 0, 0),
    };

    private static TextBlock Note(string text) => new() { Text = text, Opacity = 0.7, FontSize = 13, TextWrapping = TextWrapping.Wrap };
}
