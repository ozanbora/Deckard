using System.Globalization;
using Deckard.Core.Builds;
using Deckard.Core.Items;
using Deckard.Perception.Builds;
using Deckard.Perception.Tooltips;
using Deckard.Perception.Paragon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Deckard.App;

public sealed partial class MainWindow : Window
{
    // The account's builds, most recent first (the first loads on start); and a mark that it has seen the guide.
    private static string? LibraryFile => AccountFile("builds.json");
    private static string? GuideSeenFile => AccountFile("guide-seen");
    private static string? AccountFile(string name) => TradeClient.Shared.AccountFolder is { } folder ? Path.Combine(folder, name) : null;

    private static readonly HttpClient Http = CreateHttp();

    private readonly ItemFeed _feed;
    private AffixCatalog? _catalog;
    private BuildPlan? _plan;

    public MainWindow(ItemFeed feed)
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 820));
        _feed = feed;
        _feed.AutoStepChosen += (paragon, step) => DispatcherQueue.TryEnqueue(() => OnAutoStep(paragon, step));
        _feed.SpentRead += spent => DispatcherQueue.TryEnqueue(() =>
        {
            _spent = spent;
            SaveSteps(); // so the docked panel, which shows only the points available, still knows the points spent
        });

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        ColorCaptionButtons();
        AppWindow.Closing += (_, _) => SaveWindowState();
        TradeClient.Shared.SessionChanged += ShowAccount;
        SettingsPanel.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SettingsPanel.AccountDeleted += deleteLocal =>
        {
            var folder = TradeClient.Shared.AccountFolder;
            OnSignOutClick(this, new RoutedEventArgs()); // the server forgot the session's account: sign out here too
            if (deleteLocal && folder is not null)
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    ErrorLog.Write("deleting the account folder", e); // left behind; the next sign-in is a new account anyway
                }
            }
        };
        // A session the server turned down (expired, or a new signing key): signed out, back to the sign-in window.
        TradeClient.Shared.SessionRejected += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (TradeClient.Shared.SignedIn)
                OnSignOutClick(this, new RoutedEventArgs());
        });
        // Signed in as another player: their own build replaces the last one (the data stays loaded).
        TradeClient.Shared.SessionChanged += async () =>
        {
            if (_catalog is null || !TradeClient.Shared.SignedIn)
                return;
            ClearBuild();
            await ShowAccountBuildAsync();
        };
        ShowAccount();

        // The feed raises events on its background thread.
        feed.StatusChanged += status => DispatcherQueue.TryEnqueue(() => ShowStatus(status));
        Trade.WaitingOffersChanged += waiting =>
        {
            OffersBadge.Value = waiting;
            OffersBadge.Visibility = waiting > 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        RealmHeader.Content = Trade.RealmBox; // the realm every Trade page works on, kept in sight above them
        feed.GameWindowChanged += hwnd => DispatcherQueue.TryEnqueue(() => Trade.SetInGame(hwnd != 0)); // reported to traders when shown
        feed.ZoneRead += zone => DispatcherQueue.TryEnqueue(() => Trade.SetZone(zone)); // shared with accepted trade partners only
        feed.RealmRead += realm => DispatcherQueue.TryEnqueue(() => Trade.SetGameRealm(realm));
        // The title bar's status cuts long text off; the error also takes the build's place, in full.
        feed.LoadFailed += message => DispatcherQueue.TryEnqueue(() =>
        {
            DataLoading.Visibility = Visibility.Collapsed;
            BuildEmptyText.Text = message;
            BuildEmptyText.Foreground = ItemCard.Bad;
            BuildEmptyText.Visibility = Visibility.Visible;
        });
        feed.ItemRead += (item, _) => DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                Trade.SetLastItem(item); // offered for listing in the Trade tab
                Stash.Add(item);         // sorted, while a stash session runs
                NoteSocketedRunes(item);
            }
            catch (Exception e)
            {
                ErrorLog.Write("item read", e); // in a dispatcher callback it would end the process
            }
        });
        // Misread values the player corrected, on the item card or the Stash page: kept for the account.
        void KeepCorrection(string key, ItemCorrection? correction)
        {
            var corrections = ReadCorrections.With(_feed.Corrections, key, correction);
            _feed.Corrections = corrections;
            WriteAccountJson("corrections.json", corrections);
        }
        feed.CorrectionChanged += KeepCorrection;
        Stash.Corrected += item => KeepCorrection(ReadCorrections.ItemKey(item), ReadCorrections.Of(item, DateTimeOffset.UtcNow));
        Stash.Starting += LoadLibraryForStashAsync;
        TreeView.PointChanged += OnPointChanged;
        // An item the player wears: remembered for the account, and the doll and the to-do list follow.
        feed.EquippedRead += tooltip => DispatcherQueue.TryEnqueue(() =>
        {
            if (_catalog is null || WornItem.From(tooltip, DateTimeOffset.UtcNow) is not { } item)
                return;
            try
            {
                _worn = Gear.Remember(_worn, item, _feed.Plan, _catalog);
                WriteAccountJson("worn.json", _worn);
                NoteSocketedRunes(tooltip);
                if (_plan is not null && VariantBox.SelectedIndex >= 0)
                    ShowVariant(_plan.Variants[VariantBox.SelectedIndex]);
            }
            catch (Exception e)
            {
                ErrorLog.Write("worn item", e); // in a dispatcher callback it would end the process
            }
        });
        // A skill's tooltip on the in-game tree: the ranks items give it, kept so its points stay known after a restart.
        feed.SkillBonusRead += (node, bonus) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_feed.SkillBonuses?.TryGetValue(node, out var had) == true && had == bonus)
                return;
            var bonuses = new Dictionary<int, int>(_feed.SkillBonuses ?? new Dictionary<int, int>()) { [node] = bonus };
            _feed.SkillBonuses = bonuses;
            WriteAccountJson("skill-ranks.json", bonuses);
        });
        // The ranks the tree shows, kept so a skill out of view (or after a restart) keeps the rank it last showed.
        feed.SkillRankRead += (node, rank) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_feed.SkillRanks?.TryGetValue(node, out var had) == true && had == rank)
                return;
            var ranks = new Dictionary<int, int>(_feed.SkillRanks ?? new Dictionary<int, int>()) { [node] = rank };
            _feed.SkillRanks = ranks;
            WriteAccountJson("tree-ranks.json", ranks);
        });
        // A rune's tooltip: how many the account has, kept for the item card's runeword advice.
        feed.RuneRead += (key, count) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_feed.OwnedRunes?.GetValueOrDefault(key) == count && _feed.OwnedRunes.ContainsKey(key))
                return;
            var runes = new Dictionary<string, int>(_feed.OwnedRunes ?? new Dictionary<string, int>()) { [key] = count };
            _feed.OwnedRunes = runes;
            WriteAccountJson("runes.json", runes);
        });
        Sheet.FindOnMarket += async (kind, name) =>
        {
            Nav.SelectedItem = NavTrade; // the Search page, which the search fills in
            await Trade.FindGoodsAsync(kind, name);
        };
        Sheet.TaskTicked += (key, done) =>
        {
            if (!(done ? _ticked.Add(key) : _ticked.Remove(key)))
                return;
            WriteAccountJson("crafting-done.json", _ticked);
        };
        feed.Ready += catalog => DispatcherQueue.TryEnqueue(async () =>
        {
            _catalog = catalog;
            Trade.SetCatalog(catalog);
            LoadButton.IsEnabled = true;
            DataLoading.Visibility = Visibility.Collapsed;
            BuildEmptyText.Visibility = Visibility.Visible; // until a build shows
            await ShowAccountBuildAsync();
        });
    }

    /// <summary>Shows the Trade tab at the last item read, ready to list (from the item card over the game).</summary>
    public void ShowTradeSell()
    {
        Nav.SelectedItem = NavListings; // shows the page through OnNavChanged
    }

    /// <summary>The Trade tab, whose trade notices the app shows over the game.</summary>
    public TradeView TradeTab => Trade;

    private void ShowStatus(string status)
    {
        StatusText.Text = status;
        ToolTipService.SetToolTip(StatusText, status);
        // Green while reading the game, amber while waiting for it, red when Deckard can't see it, neutral otherwise.
        StatusDot.Fill = status is ItemFeed.ExclusiveFullscreen or ItemFeed.Minimized ? ItemCard.Bad
            : status.StartsWith("Reading", StringComparison.Ordinal) ? ItemCard.Good
            : status.StartsWith("Waiting", StringComparison.Ordinal) ? ItemCard.Fair
            : ItemCard.Bullet;
    }

    /// <summary>The player chose to restart into the downloaded update.</summary>
    public event Action? RestartForUpdate;

    /// <summary>Says an update has downloaded, with a button to restart into it (otherwise it's applied on exit).</summary>
    public void ShowUpdateReady(string version)
    {
        UpdateBar.Message = $"Deckard {version} is downloaded. Restart to use it, or it's installed when Deckard exits.";
        UpdateBar.IsOpen = true;
    }

    private void OnRestartForUpdateClick(object sender, RoutedEventArgs e) => RestartForUpdate?.Invoke();

    /// <summary>The player signed out from the title bar; the app returns to the sign-in window.</summary>
    public event Action? SignedOut;

    private void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        SaveWindowState(); // while the account folder is still known
        TradeClient.Shared.SignOut();
        SignedOut?.Invoke();
    }

    private void ShowAccount()
    {
        AccountName.Text = TradeClient.Shared.BattleTag ?? "Not signed in";
        AccountButton.Visibility = TradeClient.Shared.SignedIn ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnAboutClick(object sender, RoutedEventArgs e) => await AboutDialog.ShowAsync(Root.XamlRoot);

    private FeedbackWindow? _feedback;

    private void OnFeedbackClick(object sender, RoutedEventArgs e)
    {
        if (_feedback is null)
        {
            _feedback = new FeedbackWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), AccountBox);
            _feedback.Closed += (_, _) => _feedback = null;
        }
        _feedback.Activate();
    }

    /// <summary>Where the account button (the player's BattleTag) is, in window pixels, for covering it in screenshots.</summary>
    private Deckard.Core.Capture.PixelRect? AccountBox()
    {
        if (AccountButton.XamlRoot is not { } root)
            return null;
        var bounds = AccountButton.TransformToVisual(Root).TransformBounds(new Windows.Foundation.Rect(0, 0, AccountButton.ActualWidth, AccountButton.ActualHeight));
        var scale = root.RasterizationScale;
        const int pad = 6; // the capture and the client area can differ by a border's width
        return new((int)(bounds.X * scale) - pad, (int)(bounds.Y * scale) - pad, (int)(bounds.Width * scale) + 2 * pad, (int)(bounds.Height * scale) + 2 * pad);
    }

    // Where the window was and which page it showed, per account (window.json), so it opens the way the player left it.
    private sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized, string? Page);
    private static string? WindowFile => AccountFile("window.json");

    public void SaveWindowState()
    {
        if (WindowFile is not { } file || !AppWindow.IsVisible)
            return;
        var maximized = AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized };
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized })
            return; // a minimized window's place says nothing about where it belongs
        var page = Nav.SelectedItem == Nav.SettingsItem ? "Settings" : (Nav.SelectedItem as NavigationViewItem)?.Tag as string;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new WindowPlacement(
                AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height, maximized, page)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Opening at the default place next time is harmless.
        }
    }

    /// <summary>Puts the window where this account left it, unless that place is on no screen any more.</summary>
    public void RestoreWindowState()
    {
        WindowPlacement? placement = null;
        try
        {
            if (WindowFile is { } file && File.Exists(file))
                placement = System.Text.Json.JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
        }
        if (placement is null)
            return;
        var rect = new Windows.Graphics.RectInt32(placement.X, placement.Y, Math.Max(placement.Width, 640), Math.Max(placement.Height, 480));
        if (Microsoft.UI.Windowing.DisplayArea.GetFromRect(rect, Microsoft.UI.Windowing.DisplayAreaFallback.None) is not null)
            AppWindow.MoveAndResize(rect);
        if (placement.Maximized && AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.Maximize();
        // Before the first show the menu isn't built yet, and a page set then leaves Items selected too.
        void SelectPage() => Nav.SelectedItem = placement.Page == "Settings" ? Nav.SettingsItem
            : Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == placement.Page) ?? Nav.SelectedItem;
        if (Nav.IsLoaded)
            SelectPage();
        else
        {
            void OnLoaded(object sender, RoutedEventArgs e)
            {
                Nav.Loaded -= OnLoaded;
                SelectPage();
            }
            Nav.Loaded += OnLoaded;
        }
    }

    private void OnNavChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs e)
    {
        var page = e.IsSettingsSelected ? "Settings" : (e.SelectedItem as NavigationViewItem)?.Tag as string;
        Telemetry.Track("page_opened", ("page", page));
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        ItemsPage.Visibility = page == "Items" ? Visibility.Visible : Visibility.Collapsed;
        TalismanPage.Visibility = page == "Talisman" ? Visibility.Visible : Visibility.Collapsed;
        MercenariesPage.Visibility = page == "Mercenaries" ? Visibility.Visible : Visibility.Collapsed;
        TreePage.Visibility = page == "Tree" ? Visibility.Visible : Visibility.Collapsed;
        ParagonPage.Visibility = page == "Paragon" ? Visibility.Visible : Visibility.Collapsed;
        // The four trade entries are pages of the one TradeView: "Trade:Search", "Trade:Offers"...
        var trade = page?.StartsWith("Trade:", StringComparison.Ordinal) == true && Enum.TryParse<Deckard.App.TradePage>(page[6..], out var tradePage) ? tradePage : (Deckard.App.TradePage?)null;
        TradePage.Visibility = trade is null ? Visibility.Collapsed : Visibility.Visible;
        if (trade is { } shown)
            Trade.ShowPage(shown);
        StashPage.Visibility = page == "Stash" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The Builds menu: the account's builds (the shown one checked), then add one or remove the shown one.</summary>
    private void OnBuildsMenuOpening(object? sender, object e)
    {
        BuildsMenu.Items.Clear();
        foreach (var build in _library)
        {
            var item = new ToggleMenuFlyoutItem { Text = build.Name, IsChecked = SameLink(build.Link, _link) };
            item.Click += async (_, _) => await LoadBuildAsync(build.Link, build.Variant);
            BuildsMenu.Items.Add(item);
        }
        BuildsMenu.Items.Add(new MenuFlyoutSeparator());
        var add = new MenuFlyoutItem { Text = "Add a build…", Icon = new FontIcon { Glyph = "\uE710" } };
        add.Click += (_, _) =>
        {
            BuildSummary.Visibility = Visibility.Collapsed;
            BuildInput.Visibility = CancelAddButton.Visibility = Visibility.Visible;
            GuideBox.Text = "";
            GuideBox.Focus(FocusState.Programmatic);
        };
        BuildsMenu.Items.Add(add);
        var remove = new MenuFlyoutItem { Text = "Remove this build", Icon = new FontIcon { Glyph = "\uE74D" } };
        remove.Click += async (_, _) =>
        {
            _library.RemoveAll(b => SameLink(b.Link, _link));
            WriteLibrary();
            if (_library is [var next, ..])
                await LoadBuildAsync(next.Link, next.Variant);
            else
                ClearBuild();
        };
        BuildsMenu.Items.Add(remove);
    }

    private void OnCancelAddClick(object sender, RoutedEventArgs e)
    {
        BuildInput.Visibility = CancelAddButton.Visibility = Visibility.Collapsed;
        BuildSummary.Visibility = Visibility.Visible;
    }

    /// <summary>The title bar is ours, so the window's caption buttons take the app's text color.</summary>
    private void ColorCaptionButtons()
    {
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 178, 172, 162);
        bar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(40, 255, 255, 255);
    }

    private async void OnLoadClick(object sender, RoutedEventArgs e) => await LoadBuildAsync(GuideBox.Text, null);

    private async void OnGuideKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && LoadButton.IsEnabled)
            await LoadBuildAsync(GuideBox.Text, null);
    }

    private void OnVariantChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_plan is null || VariantBox.SelectedIndex < 0)
            return;
        ShowVariant(_plan.Variants[VariantBox.SelectedIndex]);
        Remember(_link!, VariantBox.SelectedIndex, _plan.Name);
    }

    private async Task LoadBuildAsync(string link, int? variant)
    {
        if (_catalog is null || string.IsNullOrWhiteSpace(link))
            return;
        if (!_library.Any(b => SameLink(b.Link, link)) && _library.Count >= Supporter.LibraryLimit)
        {
            await ShowLibraryFullAsync();
            return;
        }
        LoadButton.IsEnabled = false;
        InputLoading.IsActive = SummaryLoading.IsActive = true;
        try
        {
            var plan = await BuildPlanLoader.LoadAsync(link, Http, _catalog);
            if (plan.Variants.Count == 0)
                throw new FormatException("The planner has no build variants.");
            _plan = plan;
            _link = link.Trim(); // before the variant is set: OnVariantChanged files the build under it
            Telemetry.Track("build_loaded"); // not which build: build links stay on the player's PC
            BuildName.Text = plan.Name;
            ToolTipService.SetToolTip(OpenBuildButton, BuildPage(_link) is { } page ? $"Open the build on {page.Host.Replace("www.", "")}" : null);
            VariantBox.ItemsSource = plan.Variants.Select(v => v.Name).ToList();
            VariantBox.SelectedIndex = variant is { } v && v < plan.Variants.Count ? v : plan.DefaultVariant; // shows it via OnVariantChanged
            BuildInput.Visibility = CancelAddButton.Visibility = Visibility.Collapsed;
            BuildSummary.Visibility = Visibility.Visible;
            if (plan.Season != _catalog.Season)
                StatusText.Text = $"This build is for season {plan.Season}, but the item data is for season {_catalog.Season}. Item fit may be off.";
            else if (plan.Variants.All(v => v.SkillSteps.Count == 0))
                StatusText.Text = NoSkillPoints;
        }
        catch (Exception e) when (e is HttpRequestException or FormatException or System.Text.Json.JsonException or KeyNotFoundException or TaskCanceledException)
        {
            StatusText.Text = $"Couldn't load the build: {e.Message}";
        }
        finally
        {
            LoadButton.IsEnabled = true;
            InputLoading.IsActive = SummaryLoading.IsActive = false;
        }
    }

    private void ShowVariant(BuildVariant variant)
    {
        var catalog = _catalog!;
        variant = WithSkillEdits(variant);
        // The feed gets the plan from ShowSteps, with the steps saved for it: given here first, it took the step of the
        // build shown before (none: the last step) and kept it, as the same plan again isn't a change.
        _variant = variant;
        var className = variant.ClassIndex < catalog.Classes.Count ? catalog.Classes[variant.ClassIndex] : "?";
        BuildInfo.Text = string.Join(" · ", new[] { className, variant.Level > 0 ? $"level {variant.Level}" : null, $"season {_plan!.Season}" }.OfType<string>());
        BuildEmptyText.Visibility = Visibility.Collapsed;
        var worn = Gear.Assign(_worn, variant, catalog);
        Inventory.Show(variant, catalog, worn);
        Sheet.Show(variant, catalog, className, worn, _ticked);
        Talisman.Show(variant, catalog);
        Mercenaries.Show(variant, catalog);
        Trade.SetBuild(variant); // its items become quick searches
        _tree = catalog.SkillTrees.GetValueOrDefault(className) ?? new SkillTree([], []);
        ShowSteps(variant);
    }

    // Leveling steps: the planner's skill points and paragon boards step by step. The chosen steps are kept per account
    // and build (steps.json: "link#variant" -> [skill step, paragon step]); the last step, with every point, by default.
    private BuildVariant? _variant;
    private SkillTree _tree = new([], []);
    private Dictionary<string, int[]> _steps = [];
    private bool _fillingSteps;

    /// <summary>The page a build link opens: the link itself, or for a bare planner id its maxroll planner.</summary>
    private static Uri? BuildPage(string? link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri
        : BuildPlanLoader.ParsePlannerLink("maxroll.gg/d4/planner/" + link) is { } planner ? new Uri($"https://maxroll.gg/d4/planner/{planner.Id}")
        : null;

    private void OnOpenBuild(object sender, RoutedEventArgs e)
    {
        if (BuildPage(_link) is { } page)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
    }

    private string StepKey =>$"{_link}#{VariantBox.SelectedIndex}";

    /// <summary>d4builds keeps skill points in its own node order, which Deckard can't read (see <c>D4BuildsImport</c>).</summary>
    private string NoSkillPoints => D4BuildsImport.ParseLink(_link ?? "") is not null
        ? "This build has no skill points: d4builds doesn't share them. Import the same build from maxroll or Icy Veins' planner to see its skill tree."
        : "This build has no skill points.";

    // Each step picker starts with "Automatic": the in-game highlight then follows the points spent (the feed reads them
    // and picks the step, OnAutoStep). Saved as -2 in steps.json; a build without a saved choice is automatic.
    private const int Automatic = -2;
    private int? _autoSkill, _autoParagon; // the steps the feed picked, while automatic
    private int? _spent; // the skill points spent last read on the full tree: the docked panel doesn't show them

    private string SkillStepLabel(BuildVariant variant, int i) =>
        $"{i + 1}. {variant.SkillSteps[i].Name} ({variant.SkillSteps[i].RanksByNode.Values.Sum()} points)";

    private string ParagonStepLabel(BuildVariant variant, int i) => $"{i + 1}. {variant.ParagonSteps[i].Name}";

    private List<string> SkillStepItems(BuildVariant variant) =>
        [_autoSkill is int a && a < variant.SkillSteps.Count ? $"Automatic: {SkillStepLabel(variant, a)}" : "Automatic (from the points spent in game)",
         .. Enumerable.Range(0, variant.SkillSteps.Count).Select(i => SkillStepLabel(variant, i))];

    private List<string> ParagonStepItems(BuildVariant variant) =>
        [_autoParagon is int a && a < variant.ParagonSteps.Count ? $"Automatic: {ParagonStepLabel(variant, a)}" : "Automatic (from the points spent in game)",
         .. Enumerable.Range(0, variant.ParagonSteps.Count).Select(i => ParagonStepLabel(variant, i))];

    /// <summary>The step a picker stands for: automatic is the feed's pick, the last step until it has one.</summary>
    private static int StepOf(int boxIndex, int? auto, int count) =>
        count == 0 ? -1 : boxIndex <= 0 ? Math.Clamp(auto ?? count - 1, 0, count - 1) : Math.Min(boxIndex - 1, count - 1);

    private int SkillStepIndex => StepOf(TreeStepBox.SelectedIndex, _autoSkill, _variant?.SkillSteps.Count ?? 0);
    private int ParagonStepIndex => StepOf(ParagonStepBox.SelectedIndex, _autoParagon, _variant?.ParagonSteps.Count ?? 0);

    /// <summary>The feed picked a step from the points spent: the picker says which, and the tab shows it.</summary>
    private void OnAutoStep(bool paragon, int step)
    {
        if (_variant is not { } variant)
            return;
        _fillingSteps = true;
        if (paragon)
        {
            _autoParagon = step;
            var selected = ParagonStepBox.SelectedIndex;
            ParagonStepBox.ItemsSource = ParagonStepItems(variant);
            ParagonStepBox.SelectedIndex = selected;
        }
        else
        {
            _autoSkill = step;
            var selected = TreeStepBox.SelectedIndex;
            TreeStepBox.ItemsSource = SkillStepItems(variant);
            TreeStepBox.SelectedIndex = selected;
        }
        _fillingSteps = false;
        SaveSteps(); // kept, so a restart (or the docked panels, which don't show the points) starts from it
        // The feed already has every step and picks itself: only the tab follows (feeding the plan again would reset it).
        if (paragon && ParagonStepBox.SelectedIndex == 0)
            ShowParagonStep(feed: false);
        if (!paragon && TreeStepBox.SelectedIndex == 0)
            ShowSkillStep(feed: false);
    }

    private void ShowSteps(BuildVariant variant)
    {
        var saved = _steps.GetValueOrDefault(StepKey);
        _fillingSteps = true;
        // The automatic steps last read, saved after the choices ([skill, paragon, auto skill, auto paragon, points spent];
        // -1 for none).
        int? Remembered(int i) => saved is not null && saved.Length > i && saved[i] >= 0 ? saved[i] : null;
        (_autoSkill, _autoParagon, _spent) = (Remembered(2), Remembered(3), Remembered(4));
        TreeStepBox.ItemsSource = SkillStepItems(variant);
        // Editing works on the whole build: its last step.
        TreeStepBox.SelectedIndex = variant.SkillSteps.Count == 0 ? -1
            : TreeView.Editing ? variant.SkillSteps.Count
            : saved?[0] is not { } skill || skill == Automatic ? 0 : Math.Clamp(skill, 0, variant.SkillSteps.Count - 1) + 1;
        TreeStepBox.IsEnabled = !TreeView.Editing;
        ParagonStepBox.ItemsSource = ParagonStepItems(variant);
        ParagonStepBox.SelectedIndex = variant.ParagonSteps.Count == 0 ? -1
            : saved?[1] is not { } board || board == Automatic ? 0 : Math.Clamp(board, 0, variant.ParagonSteps.Count - 1) + 1;
        _fillingSteps = false;
        // A single step is the whole build: nothing to choose.
        TreeStepPanel.Visibility = variant.SkillSteps.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        TreeEmptyText.Visibility = variant.SkillSteps.Count == 0 && !TreeView.Editing ? Visibility.Visible : Visibility.Collapsed;
        TreeEmptyText.Text = NoSkillPoints;
        TreeEditPanel.Visibility = _tree.Nodes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var edited = _skillEdits.ContainsKey(StepKey);
        TreeResetButton.Visibility = edited ? Visibility.Visible : Visibility.Collapsed;
        TreeEditInfo.Text = $"{variant.SkillSteps.LastOrDefault()?.RanksByNode.Values.Sum() ?? 0} points, " + (edited ? "your own." : "as the guide has them.")
            + (TreeView.Editing ? " Click a skill to add a point, right-click to remove one." : "");
        // With one step there's nothing to choose, but its checklist still shows.
        ParagonStepPanel.Visibility = variant.ParagonSteps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ParagonStepBox.Visibility = variant.ParagonSteps.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ShowSkillStep();
        ShowParagonStep();
    }

    /// <summary>The tree at the chosen step, green where it adds points; the in-game highlight follows the same step.</summary>
    private void ShowSkillStep(bool feed = true)
    {
        if (_variant is not { } variant)
            return;
        var steps = variant.SkillSteps;
        var i = SkillStepIndex;
        if (i < 0 || i >= steps.Count)
        {
            TreeView.Show(_tree, new Dictionary<int, int>());
            return;
        }
        var previous = steps.Count > 1 && i > 0 ? steps[i - 1].RanksByNode : null;
        TreeView.Show(_tree, steps[i].RanksByNode, steps.Count > 1 ? previous ?? new Dictionary<int, int>() : null);
        if (feed)
            FeedSteps();
        var nodes = _tree.Nodes.ToDictionary(n => n.Node);
        var adds = steps[i].RanksByNode
            .Select(r => (Node: nodes.GetValueOrDefault(r.Key), Gain: r.Value - (previous?.GetValueOrDefault(r.Key) ?? 0)))
            .Where(a => a.Node is not null && a.Gain > 0).Select(a => $"{a.Node!.Name} +{a.Gain}").ToList();
        TreeStepAdds.Text = adds.Count == 0 ? "" : "This step adds: " + string.Join(", ", adds.Take(10)) + (adds.Count > 10 ? $" and {adds.Count - 10} more" : "") + ".";
    }

    private void ShowParagonStep(bool feed = true)
    {
        var steps = _variant?.ParagonSteps ?? [];
        var i = ParagonStepIndex;
        ParagonBoards.Show(_catalog!.Paragon, i >= 0 && i < steps.Count ? steps[i].Boards : []);
        // The order to attach the boards, their turns and glyphs: what the in-game paragon highlight walks through.
        ParagonChecklist.Text = i >= 0 && i < steps.Count ? string.Join("\n", ParagonGuide.Checklist(steps[i], _catalog.Paragon)) : "";
        if (feed)
            FeedSteps();
    }

    /// <summary>
    /// The in-game highlights show the chosen steps: the build up to them goes to the feed; with Automatic, every step,
    /// and the feed picks from the points spent.
    /// </summary>
    private void FeedSteps()
    {
        if (_variant is not { } variant)
            return;
        bool autoSkill = TreeStepBox.SelectedIndex == 0, autoParagon = ParagonStepBox.SelectedIndex == 0;
        int skill = SkillStepIndex, paragon = ParagonStepIndex;
        _feed.AutoSkillStep = autoSkill;
        _feed.AutoParagonStep = autoParagon;
        (_feed.RememberedSkillStep, _feed.RememberedParagonStep, _feed.RememberedSpent) = (_autoSkill, _autoParagon, _spent);
        _feed.Plan = variant with
        {
            SkillSteps = autoSkill || skill < 0 ? variant.SkillSteps : [.. variant.SkillSteps.Take(skill + 1)],
            ParagonSteps = autoParagon || paragon < 0 ? variant.ParagonSteps : [.. variant.ParagonSteps.Take(paragon + 1)],
        };
    }

    private void OnTreeStepChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingSteps)
            return;
        ShowSkillStep();
        SaveSteps();
    }

    private void OnParagonStepChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingSteps)
            return;
        ShowParagonStep();
        SaveSteps();
    }

    private void SaveSteps()
    {
        if (_link is null)
            return;
        int Saved(int boxIndex) => boxIndex == 0 ? Automatic : boxIndex - 1;
        _steps[StepKey] = [Saved(TreeStepBox.SelectedIndex), Saved(ParagonStepBox.SelectedIndex), _autoSkill ?? -1, _autoParagon ?? -1, _spent ?? -1];
        WriteAccountJson("steps.json", _steps);
    }

    // The player's own skill points, per account and build (skill-edits.json: "link#variant" -> node -> rank). They
    // replace the guide's last step, so the tree, the build sheet and the in-game highlight all follow them.
    private Dictionary<string, Dictionary<int, int>> _skillEdits = [];

    private BuildVariant WithSkillEdits(BuildVariant variant)
    {
        if (!_skillEdits.TryGetValue(StepKey, out var ranks))
            return variant;
        var own = new SkillStep("Your points", ranks);
        return variant with { SkillSteps = variant.SkillSteps.Count == 0 ? [own] : [.. variant.SkillSteps.SkipLast(1), own] };
    }

    private void OnTreeEditClick(object sender, RoutedEventArgs e)
    {
        TreeView.Editing = TreeEditButton.IsChecked == true;
        if (_variant is not null)
            ShowSteps(_variant);
    }

    private void OnPointChanged(int node, bool add)
    {
        if (_variant is null || _plan is null || VariantBox.SelectedIndex < 0)
            return;
        var ranks = _variant.SkillSteps.LastOrDefault()?.RanksByNode ?? new Dictionary<int, int>();
        var changed = SkillPoints.Change(_tree, ranks, node, add);
        if (changed.Count == ranks.Count && changed.All(r => ranks.GetValueOrDefault(r.Key) == r.Value))
        {
            var name = _tree.Nodes.FirstOrDefault(n => n.Node == node)?.Name;
            TreeEditInfo.Text = add ? $"{name} can't take a point: it's at its limit, or nothing taken leads to it yet."
                : $"{name} keeps its point: something you took hangs from it.";
            return;
        }
        _skillEdits[StepKey] = changed;
        WriteAccountJson("skill-edits.json", _skillEdits);
        ShowVariant(_plan.Variants[VariantBox.SelectedIndex]);
    }

    private void OnTreeResetClick(object sender, RoutedEventArgs e)
    {
        if (_plan is null || VariantBox.SelectedIndex < 0 || !_skillEdits.Remove(StepKey))
            return;
        WriteAccountJson("skill-edits.json", _skillEdits);
        ShowVariant(_plan.Variants[VariantBox.SelectedIndex]);
    }

    /// <summary>
    /// Runes seen socketed in an item: the first time they show in it, they leave the spare runes saved (the player put
    /// them in after their last count, or the count would already lack them), so the advice doesn't offer them again. Kept
    /// per item name in runes-socketed.json; hovering the rune in the stash sets its true count again.
    /// </summary>
    private void NoteSocketedRunes(Deckard.Perception.Tooltips.ItemTooltip item)
    {
        if (_catalog is null || item.Name is not { } name)
            return;
        var keys = Deckard.Perception.Builds.Runewords.SocketedRunes(item, _catalog).Order(StringComparer.Ordinal).ToList();
        var before = _socketedRunes.GetValueOrDefault(name) ?? [];
        if (keys.SequenceEqual(before))
            return;
        _socketedRunes[name] = keys;
        WriteAccountJson("runes-socketed.json", _socketedRunes);
        var added = keys.ToList();
        foreach (var key in before)
            added.Remove(key); // only the runes new to this item
        if (added.Count == 0 || _feed.OwnedRunes is not { } owned)
            return;
        var runes = new Dictionary<string, int>(owned);
        foreach (var key in added.Where(runes.ContainsKey))
            runes[key] = Math.Max(0, runes[key] - 1);
        _feed.OwnedRunes = runes;
        WriteAccountJson("runes.json", runes);
    }

    private Dictionary<string, List<string>> _socketedRunes = [];

    /// <summary>The account's most recent build; an account without one gets the guide the first time.</summary>
    private async Task ShowAccountBuildAsync()
    {
        _worn = ReadAccountJson<List<WornItem>>("worn.json") ?? [];
        _steps = ReadAccountJson<Dictionary<string, int[]>>("steps.json") ?? [];
        _skillEdits = ReadAccountJson<Dictionary<string, Dictionary<int, int>>>("skill-edits.json") ?? [];
        _ticked = ReadAccountJson<HashSet<string>>("crafting-done.json") ?? [];
        _feed.OwnedRunes = ReadAccountJson<Dictionary<string, int>>("runes.json"); // null until a rune's tooltip is read
        _socketedRunes = ReadAccountJson<Dictionary<string, List<string>>>("runes-socketed.json") ?? [];
        _feed.Corrections = ReadAccountJson<Dictionary<string, ItemCorrection>>("corrections.json"); // misread values the player corrected
        _feed.SkillBonuses = ReadAccountJson<Dictionary<int, int>>("skill-ranks.json"); // skills' item ranks, off their tooltips
        _feed.SkillRanks = ReadAccountJson<Dictionary<int, int>>("tree-ranks.json");    // the ranks the tree last showed
        await AskTelemetryOnceAsync();
        await Supporter.RefreshAsync(); // its limits apply to the library and stash sorting
        _library = ReadLibrary();
        if (_library is [var last, ..])
        {
            GuideBox.Text = last.Link;
            await LoadBuildAsync(last.Link, last.Variant);
        }
        else if (GuideSeenFile is { } seen && !File.Exists(seen))
            ShowGuide();
    }

    private void ClearBuild()
    {
        _plan = null;
        _link = null;
        _feed.Plan = null;
        GuideBox.Text = "";
        VariantBox.ItemsSource = null;
        BuildSummary.Visibility = CancelAddButton.Visibility = Visibility.Collapsed;
        BuildInput.Visibility = BuildEmptyText.Visibility = Visibility.Visible;
        TreeEmptyText.Visibility = TreeEditPanel.Visibility = Visibility.Collapsed;
        Inventory.Clear();
        Sheet.Clear();
        Talisman.Clear();
        Mercenaries.Clear();
        TreeView.Show(new SkillTree([], []), new Dictionary<int, int>());
        ParagonBoards.Show(_catalog!.Paragon, []);
        Trade.SetBuild(null);
    }

    // The account's worn items (read from "EQUIPPED" tooltips) and the crafting tasks it ticked off; both per account.
    private List<WornItem> _worn = [];
    private HashSet<string> _ticked = [];

    private static T? ReadAccountJson<T>(string name) where T : class
    {
        try
        {
            return AccountFile(name) is { } file && File.Exists(file) ? System.Text.Json.JsonSerializer.Deserialize<T>(File.ReadAllText(file)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null; // unreadable: start over; losing a worn-item record only means hovering the item again
        }
    }

    private void WriteAccountJson<T>(string name, T value)
    {
        if (AccountFile(name) is not { } file)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(value));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"Couldn't save {name}: {e.Message}");
        }
    }

    // Plans of the library's builds, by link, so a stash session doesn't load them again.
    private readonly Dictionary<string, BuildPlan> _plans = [];

    /// <summary>
    /// Gives the Stash page every library build at its saved variant (a free account: the shown build), loading plans
    /// not loaded yet.
    /// </summary>
    private async Task LoadLibraryForStashAsync()
    {
        if (_catalog is not { } catalog)
            return;
        var builds = new List<(string, BuildVariant)>();
        foreach (var saved in Supporter.SortsAllBuilds ? _library : _library.Where(b => SameLink(b.Link, _link)))
        {
            try
            {
                if (!_plans.TryGetValue(saved.Link, out var plan))
                    _plans[saved.Link] = plan = await BuildPlanLoader.LoadAsync(saved.Link, Http, catalog);
                if (saved.Variant >= 0 && saved.Variant < plan.Variants.Count)
                    builds.Add(($"{plan.Name} ({plan.Variants[saved.Variant].Name})", plan.Variants[saved.Variant]));
            }
            catch (Exception e) when (e is HttpRequestException or FormatException or System.Text.Json.JsonException or KeyNotFoundException or TaskCanceledException)
            {
                ShowStatus($"Couldn't load {saved.Name} for sorting: {e.Message}");
            }
        }
        Stash.SetBuilds(builds, catalog);
    }

    /// <summary>A build in the account's library: its link, the variant last shown, and its name for the Builds menu.</summary>
    private sealed record SavedBuild(string Link, int Variant, string Name);

    private List<SavedBuild> _library = [];
    private string? _link; // the shown build's link

    private static bool SameLink(string a, string? b) => string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    // builds.json couldn't be read (e.g. locked by a backup or antivirus): leave it alone this session rather than
    // overwrite the player's builds with what little this session knows.
    private bool _libraryLocked;

    private List<SavedBuild> ReadLibrary()
    {
        _libraryLocked = false;
        if (LibraryFile is not { } file)
            return [];
        try
        {
            if (File.Exists(file))
                return System.Text.Json.JsonSerializer.Deserialize<List<SavedBuild>>(File.ReadAllText(file)) ?? [];
            // The single build an earlier Deckard kept (build.txt: link, then variant) starts the library. It's only
            // deleted once the library holds it, so a failed first load (offline, maxroll down) loses nothing.
            var old = Path.Combine(Path.GetDirectoryName(file)!, "build.txt");
            if (!File.Exists(old))
                return [];
            var lines = File.ReadAllLines(old);
            _library = lines.Length >= 2 && int.TryParse(lines[1], CultureInfo.InvariantCulture, out var variant) ? [new SavedBuild(lines[0], variant, "Saved build")] : [];
            if (WriteLibrary())
                File.Delete(old);
            return _library;
        }
        catch (System.Text.Json.JsonException)
        {
            // Damaged: keep it as builds.json.bad for the player (or us) to look at, and start a new library.
            try
            {
                File.Move(file, file + ".bad", overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _libraryLocked = true; // can't move it either: leave it alone
            }
            return [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _libraryLocked = true;
            ShowStatus($"Couldn't read your builds ({e.Message}); they're left as they are until Deckard restarts.");
            return [];
        }
    }

    /// <summary>Asks once (for this Windows user) whether Deckard may send anonymous usage and crash data.</summary>
    private async Task AskTelemetryOnceAsync()
    {
        if (Telemetry.Consent is not null || !AppWindow.IsVisible)
            return; // asked already, or loading in the tray: ask when the window shows
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Help improve Deckard",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "May Deckard send anonymous usage and crash data to its server? It's counts and sizes only: which pages "
                    + "you open, how many lines of each item it read and at what screen size, and where Deckard itself crashed. "
                    + "Never screenshots, items, builds, your BattleTag or anything about the game. You can change this in Settings.",
            },
            PrimaryButtonText = "Send anonymous data",
            CloseButtonText = "Don't send",
            DefaultButton = ContentDialogButton.Primary,
        };
        Telemetry.SetConsent(await dialog.ShowAsync() == ContentDialogResult.Primary);
        if (Telemetry.Consent == true)
            Telemetry.Track("session_start");
    }

    /// <summary>A free account's library is full: says so, and offers the Patreon settings.</summary>
    private async Task ShowLibraryFullAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Your library is full",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"Free accounts keep {Supporter.FreeBuilds} builds. Remove one from the Builds menu to add this one, "
                    + "or link your Patreon in Settings: patrons keep any number of builds.",
            },
            PrimaryButtonText = "Patreon settings",
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            Nav.SelectedItem = Nav.SettingsItem;
    }

    /// <summary>Files the shown build first in the library (its variant and name as now), and saves the library.</summary>
    private void Remember(string link, int variant, string name)
    {
        _library.RemoveAll(b => SameLink(b.Link, link));
        _library.Insert(0, new SavedBuild(link, variant, name));
        WriteLibrary();
    }

    /// <summary>Saves the library; false when it couldn't (or mustn't, see <see cref="_libraryLocked"/>).</summary>
    private bool WriteLibrary()
    {
        if (LibraryFile is not { } file || _libraryLocked)
            return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(_library));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"Couldn't save your builds: {e.Message}");
            return false;
        }
    }

    // The guide: a tour of the window, one tip per part, for a new account (and from the title bar's help button).
    private TeachingTip? _guide;
    private int _guideStep;

    private (FrameworkElement Target, NavigationViewItem? Page, string Title, string Text)[] GuideSteps() =>
    [
        (BuildPanel, NavItems, "Start with a build",
            "Paste a Diablo IV build link from maxroll, d4builds or Icy Veins' planner here and import it. Deckard keeps it for your account and compares every item you read with it."),
        (NavItems, NavItems, "Items", "The build's gear, slot by slot. Hover an item to see its affixes, aspects and roll ranges."),
        (NavTalisman, NavTalisman, "Talisman", "The build's seal and charms. Hover a charm to see its set and the bonuses the build's charms reach."),
        (NavMercenaries, NavMercenaries, "Mercenaries", "Who the guide hires and which skills it picks in their tree, and the reinforcement it calls. Open the Mercenaries tab in game and Deckard says what to change."),
        (NavTree, NavTree, "Skill tree", "The build's skill points, drawn like the game's tree. Open the skill tree in game and Deckard marks the nodes to take."),
        (NavParagon, NavParagon, "Paragon", "The build's paragon boards and the nodes it takes."),
        (NavTrade, NavTrade, "Trade",
            "Search listings priced in gold and save your filters as quick searches. My listings lists the last item Deckard read, Offers is where deals are made, and Buy orders asks for items at your price. Trades finish by hand in game."),
        (StatusPill, NavItems, "Reading the game",
            "Start Diablo IV and hold Alt over an item: Deckard reads its tooltip from the screen and shows how well it fits your build. This shows what Deckard is doing."),
        (AccountButton, null, "Your account", "Your build and quick searches are kept for this Battle.net account. Sign out here."),
        (HelpButton, null, "That's the tour", "Open this guide again here whenever you like."),
    ];

    private void ShowGuide()
    {
        if (_guide is null)
        {
            _guide = new TeachingTip { CloseButtonContent = "Skip", PreferredPlacement = TeachingTipPlacementMode.Auto };
            _guide.ActionButtonClick += (_, _) =>
            {
                _guideStep++;
                if (_guideStep < GuideSteps().Length)
                    ShowGuideStep();
                else
                    _guide.IsOpen = false;
            };
            _guide.Closed += (_, _) => MarkGuideSeen();
            Root.Children.Add(_guide);
        }
        _guideStep = 0;
        ShowGuideStep();
    }

    private void ShowGuideStep()
    {
        var steps = GuideSteps();
        var (target, page, title, text) = steps[_guideStep];
        if (page is not null)
            Nav.SelectedItem = page; // show the part the tip is about
        _guide!.IsOpen = false; // reopen at the new target
        _guide.Target = target;
        _guide.Title = $"{title}  ({_guideStep + 1}/{steps.Length})";
        _guide.Subtitle = text;
        _guide.ActionButtonContent = _guideStep == steps.Length - 1 ? "Done" : "Next";
        _guide.IsOpen = true;
    }

    private static void MarkGuideSeen()
    {
        if (GuideSeenFile is not { } file)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "");
        }
        catch (IOException)
        {
            // Seeing the guide again is harmless.
        }
    }

    private void OnGuideClick(object sender, RoutedEventArgs e) => ShowGuide();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Deckard/1.0");
        return http;
    }
}
