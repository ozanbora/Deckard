using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Deckard.App;

public partial class App : Application
{
    private ItemFeed? _feed;
    private MainWindow? _main;
    private OverlayWindow? _overlay;
    private TreeHighlightOverlay? _treeOverlay;
    private TradeToastWindow? _toasts;
    private TrayIcon? _tray;
    private SignInWindow? _signIn;
    private bool _shown, _feedStarted, _exiting;
    private Updater? _updater;

    public static string IconFile => Path.Combine(AppContext.BaseDirectory, "Assets", "Deckard.ico");

    public App()
    {
        InitializeComponent();
        // An error on the UI thread that nothing caught: logged, and Deckard keeps running instead of closing.
        UnhandledException += (_, e) =>
        {
            ErrorLog.Write("ui", e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // The build and quick searches used to be kept for whoever used Deckard; they're per account now (and the theme
        // choice is gone).
        foreach (var old in new[] { "build.txt", "quick-searches.json", "theme.txt" })
        {
            try
            {
                File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deckard", old));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // No Deckard folder yet, or the file is in use or read-only: nothing to clean up now.
            }
        }
        AppSettings.KeepPerAccount();
        Telemetry.Start();
        var ui = DispatcherQueue.GetForCurrentThread();
        _feed = new ItemFeed();
        var visibility = new OverlayVisibility(ui, _feed);
        _main = new MainWindow(_feed);
        _overlay = new OverlayWindow(_feed, visibility);
        _treeOverlay = new TreeHighlightOverlay(ui, visibility);
        _feed.HighlightChanged += _treeOverlay.Post;
        _feed.HighlightChanged += highlight => ui.TryEnqueue(() => _overlay.OverBuildScreen = highlight is not null);
        // A game panel open on the right sits where trade notices go: they move left of it.
        _feed.SidePanelChanged += left => ui.TryEnqueue(() => _toasts!.DockedPanelLeft = left); // made below, before the feed starts
        _toasts = new TradeToastWindow(_feed);
        _toasts.CardShowing = () => _overlay.Showing;
        _toasts.TooltipArea = () => _overlay.TooltipArea;
        visibility.Changed += _ => ui.TryEnqueue(_toasts.Refresh); // after the card has shown or hidden
        _main.TradeTab.Notice += notice => _toasts.Post(notice);
        _main.AppWindow.SetIcon(IconFile);
        // Closing the window keeps Deckard in the tray (unless the player turned that off); Exit is in the tray's menu.
        _main.AppWindow.Closing += (_, e) =>
        {
            if (_exiting || !AppSettings.Shared.CloseToTray)
            {
                _exiting = true;
                return;
            }
            e.Cancel = true;
            _main.AppWindow.Hide();
        };
        _tray = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(_main), IconFile);
        _tray.Toggle += ToggleDeckard;
        _tray.ShowRequested += ShowDeckard;
        _tray.ExitRequested += ExitDeckard;
        _tray.SetHotkey(AppSettings.Shared.ShowHideHotkey);
        AppSettings.Shared.Changed += () => _tray.SetHotkey(AppSettings.Shared.ShowHideHotkey);
        _main.Closed += (_, _) =>
        {
            _tray.Dispose();
            _signIn?.Close();
            _feed.Dispose();
            _overlay.Close();
            _toasts.Close();
            _treeOverlay.Dispose();
        };
        _feed.GameWindowChanged += hwnd => ui.TryEnqueue(() =>
        {
            if (hwnd != 0)
                ShowGameHint();
        });
        _overlay.QueueForListing = item => _main.TradeTab.Queue(item);
        _overlay.ListRequested += () =>
        {
            if (!TradeClient.Shared.SignedIn)
                return; // the sign-in window is up; listing waits for it
            ShowMain();
            _main.ShowTradeSell();
        };
        // Once signed in and loaded, look for an update on the Deckard server (installed copies only).
        _updater = new Updater(TradeClient.Shared);
        _feed.Ready += _ => ui.TryEnqueue(async () =>
        {
            if (await _updater.DownloadAsync() is { } version)
                _main.ShowUpdateReady(version);
        });
        _main.RestartForUpdate += () =>
        {
            _exiting = true;
            _updater.RestartNow();
        };
        _main.SignedOut += () =>
        {
            _main.AppWindow.Hide();
            ShowSignIn();
        };
        // Started with Windows (--tray) and signed in: load quietly in the tray.
        if (TradeClient.Shared.SignedIn && Environment.GetCommandLineArgs().Contains(StartWithWindows.TrayArgument))
            StartFeed();
        else
            ShowDeckard();
    }

    /// <summary>The tray icon's click and the hotkey: hide Deckard when it's in front, else bring it up.</summary>
    private void ToggleDeckard()
    {
        if (TradeClient.Shared.SignedIn && _main!.AppWindow.IsVisible && OverlayWindow.Native.GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(_main))
        {
            _main.SaveWindowState();
            _main.AppWindow.Hide();
        }
        else
            ShowDeckard();
    }

    private void ShowDeckard()
    {
        if (TradeClient.Shared.SignedIn)
            ShowMain();
        else
            ShowSignIn();
    }

    private void ExitDeckard()
    {
        _exiting = true;
        _updater?.ApplyOnExit();
        Task.Run(Telemetry.SendAsync).Wait(TimeSpan.FromSeconds(2)); // the last events, if the server answers quickly
        _main!.Close(); // its Closed handler closes the rest
        Current.Exit();
    }

    /// <summary>
    /// The first time an account's Deckard finds the game: a notice over it saying how to check an item, since the guide
    /// that explained it is long gone by then.
    /// </summary>
    private void ShowGameHint()
    {
        if (TradeClient.Shared.AccountFolder is not { } folder || File.Exists(Path.Combine(folder, "game-hint-seen")) || !AppSettings.Shared.ShowItemCard)
            return;
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "game-hint-seen"), "");
        }
        catch (IOException)
        {
            return; // can't remember showing it: better not to show it on every start
        }
        var settings = AppSettings.Shared;
        var key = AppSettings.OverlayKeys.FirstOrDefault(k => k.Key == settings.OverlayKey).Name ?? "Alt";
        _toasts!.Post(new TradeNotice(
            "Deckard sees the game",
            new Microsoft.UI.Xaml.Controls.TextBlock { Text = "✠", FontSize = 22, Foreground = ItemCard.Fair },
            $"{(settings.ToggleOverlay ? "Press" : "Hold")} {key} over an item to see what to do with it for your build: keep, craft or salvage.",
            []), aboutTrading: false);
    }

    /// <summary>The game data comes from the Deckard server, so the player lands here until signed in.</summary>
    private void ShowSignIn()
    {
        if (_signIn is not null)
        {
            _signIn.Activate(); // already open: bring it forward
            return;
        }
        var signIn = _signIn = new SignInWindow();
        signIn.AppWindow.SetIcon(IconFile);
        signIn.Closed += (_, _) => _signIn = null;
        signIn.SignedIn += () =>
        {
            ShowMain(); // first, so closing the sign-in window doesn't leave Deckard with no window
            signIn.Close();
        };
        signIn.Activate();
    }

    /// <summary>Opens the main window (where this account left it), starting the feed if it hasn't yet.</summary>
    private void ShowMain()
    {
        _main!.RestoreWindowState();
        if (_shown)
        {
            if (_main.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
                presenter.Restore();
            _main.AppWindow.Show();
        }
        _shown = true;
        _main.Activate();
        StartFeed();
    }

    /// <summary>Starts loading the game data and reading the game, once, with the session signed in.</summary>
    private void StartFeed()
    {
        if (_feedStarted)
            return;
        _feedStarted = true;
        _feed!.Start();
    }
}
