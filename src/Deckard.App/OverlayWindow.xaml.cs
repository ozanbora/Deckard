using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Deckard.Core.Items;
using Deckard.Perception.Builds;
using Deckard.Perception.Tooltips;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace Deckard.App;

/// <summary>
/// Shows the last read item over the game as an <see cref="ItemCard"/>, with how it compares to the build's item at
/// the bottom. Our own topmost window, never a hook into the game: never activated (the game keeps focus), hidden
/// from the taskbar and Alt+Tab. It takes the mouse so the card can be dragged and closed; it sits at the game
/// window's top-left until dragged, then keeps its place relative to the game. Needs the game in borderless windowed
/// mode; nothing draws over exclusive fullscreen. Or, if the player chooses (<see cref="AppSettings.ItemCard"/>), an
/// ordinary Deckard window: title bar, taskbar, not topmost, its own place (e.g. a second screen), always showing the last
/// item read; it never takes the focus from the game when an item comes in.
/// </summary>
public sealed partial class OverlayWindow : Window
{
    private const int Margin = 24;
    private readonly nint _hwnd;
    private readonly OverlayVisibility _visibility;
    private nint _game;
    private AffixCatalog? _catalog;
    private readonly ItemFeed _feed; // its build and the player's runes, for the card's socket advice
    private bool _hasItem, _closed;
    private bool _editing;                                      // a value's boxes are open on the card
    private bool _pinned;                                       // hold mode: a correction began, the card stays until closed
    private (ItemTooltip Item, ItemComparison? Fit)? _last;     // the card's item
    private PointInt32? _offset;                                // where the user dragged the card, from the game's client origin
    private (PointInt32 Cursor, PointInt32 Window)? _drag;
    private readonly OverlappedPresenter _presenter;
    private bool _windowMode, _windowPlaced, _shuttingDown;

    /// <summary>The player asked to list the card's item: the Trade tab's sell preview already holds it (the last item read).</summary>
    public event Action? ListRequested;

    /// <summary>Adds the item to the Trade tab's list of items to list, and says how it went (set by the app).</summary>
    public Func<ItemTooltip, string>? QueueForListing { get; set; }

    public OverlayWindow(ItemFeed feed, OverlayVisibility visibility)
    {
        _visibility = visibility;
        _feed = feed;
        InitializeComponent();
        _presenter = OverlappedPresenter.Create();
        _presenter.IsResizable = false;
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = false;
        AppWindow.SetPresenter(_presenter);
        Title = "Deckard item card";

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Native.MakeOverlay(_hwnd);
        _windowMode = AppSettings.Shared.ItemCard != CardPlace.Window; // the opposite, so ApplyMode sets the window up
        ApplyMode();
        AppSettings.Shared.Changed += () => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyMode();
            Place();
        });
        // The window's own close button hides the card until the next item; quitting Deckard closes it for good.
        AppWindow.Closing += (_, e) =>
        {
            if (_shuttingDown)
                return;
            e.Cancel = true;
            CloseCard();
        };

        feed.Ready += catalog => DispatcherQueue.TryEnqueue(() => _catalog = catalog);
        feed.GameWindowChanged += hwnd => DispatcherQueue.TryEnqueue(() =>
        {
            _game = hwnd;
            if (hwnd == 0 && !_windowMode)
                AppWindow.Hide();
        });
        feed.ItemRead += (item, fit) => DispatcherQueue.TryEnqueue(() =>
        {
            // An exception in a dispatcher callback ends the process (no handler sees it): a card that can't be drawn is
            // logged instead.
            try
            {
                if (!_editing) // the player is correcting the card's item: a new read waits for the next hover
                    Show(item, fit);
            }
            catch (Exception e)
            {
                ErrorLog.Write("item card", e);
            }
        });

        // Another tooltip in the game: the card about the last one hides until the new one is read (a read takes up to a
        // second or two; till then the card showed the previous item over the new one).
        feed.TooltipReplaced += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (_windowMode || _drag is not null || _editing || Native.GetForegroundWindow() == _hwnd)
                return; // the player is on the card, or it's in its own window, which keeps the last item
            _hasItem = false;
            if (AppWindow.IsVisible)
                AppWindow.Hide();
        });

        // A rune: its own card, saying it's saved to the player's runes (or that it couldn't tell which rune).
        feed.RuneSeen += seen => DispatcherQueue.TryEnqueue(() =>
        {
            if (_editing)
                return;
            Panel.Child = RuneCard(seen);
            _tooltip = seen.Tooltip.Bounds;
            _hasItem = true;
            _closed = false;
            Place();
        });

        // Shown only with the overlay key over the game (see OverlayVisibility).
        visibility.Changed += _ => Place();
        AppSettings.Shared.CardPositionReset += () =>
        {
            // Back beside the game's tooltip (or, in its own window, back to its first place), and shown again if it was
            // closed (a stuck drag would block placing it).
            (_offset, _drag, _closed, _windowPlaced) = (null, null, false, false);
            Place();
        };

        // Follow the game window when it moves.
        var follow = DispatcherQueue.CreateTimer();
        follow.Interval = TimeSpan.FromMilliseconds(500);
        follow.Tick += (_, _) => Place();
        follow.Start();
    }

    /// <summary>Puts an item on the card. <paramref name="reopen"/>: a new read shows a card the player closed again.</summary>
    private void Show(ItemTooltip item, ItemComparison? fit, bool reopen = true)
    {
        _last = (item, fit);
        // A long item's card (a unique with its powers and the build's diff) can be taller than the screen: it scrolls,
        // and its trade buttons stay under it, in view.
        var card = Card(item, fit);
        var layout = new Grid { RowDefinitions = { new() { Height = new GridLength(1, GridUnitType.Star) }, new() { Height = GridLength.Auto } } };
        layout.Children.Add(new ScrollViewer
        {
            Content = card,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        if (_actions is { } actions)
        {
            var bar = new Border
            {
                Child = actions, Padding = new Thickness(16, 10, 16, 14), Background = card.Background, BorderBrush = card.BorderBrush,
                BorderThickness = new Thickness(1, 0, 1, 1), CornerRadius = new CornerRadius(0, 0, 3, 3), Width = card.Width,
            };
            Grid.SetRow(bar, 1);
            layout.Children.Add(bar);
        }
        Panel.Child = layout;
        _tooltip = item.Group ?? item.Bounds;
        _hasItem = true;
        if (reopen)
            _closed = false;
        Place();
    }

    /// <summary>
    /// Set while the skill tree or paragon highlight shows (set on the UI thread): the card would cover the game's points
    /// box, which the player and Deckard's automatic step both read, and those screens have no items to judge.
    /// </summary>
    public bool OverBuildScreen
    {
        get => _overBuildScreen;
        set
        {
            _overBuildScreen = value;
            Place();
        }
    }

    private bool _overBuildScreen;
    private StackPanel? _actions; // the item card's trade buttons, set by Footer, placed under its scrolling part
    private Deckard.Core.Capture.PixelRect? _tooltip; // the game's tooltip the card is about, in client pixels

    /// <summary>
    /// Where the card goes when the player hasn't placed it: beside the game's tooltip, on the side towards the screen's
    /// middle. The game opens a tooltip beside the panel its item is in (stash, vendor and reputation on the left,
    /// inventory on the right), so that side is the game's world, not a panel the player is reading. The top-left corner,
    /// where the card used to go, is where those left-hand panels open.
    /// </summary>
    private PointInt32? Beside(Deckard.Core.Capture.PixelRect? tooltip, int w, int h, int margin)
    {
        if (tooltip is not { } t || !Native.TryGetClientSize(_game, out var width, out var height))
            return null;
        var left = t.X + t.Width / 2 < width / 2 ? t.Right + margin : t.X - w - margin;
        return new PointInt32(Math.Clamp(left, margin, Math.Max(margin, width - w - margin)), Math.Clamp(t.Y, margin, Math.Max(margin, height - h - margin)));
    }

    private void Place()
    {
        if (_drag is not null)
            return;
        var hidden = AppSettings.Shared.ItemCard == CardPlace.Off || _closed
            || !_windowMode && (!_visibility.IsVisible || _overBuildScreen);
        if (hidden)
        {
            if (_editing)
            {
                // The game lost the focus (Alt+Tab) or the card was closed mid-correction: the boxes go, the item stays as it was.
                Editing(false);
                if (_last is var (item, fit))
                    Show(item, fit, reopen: false);
            }
            GiveFocusBack();
            if (AppWindow.IsVisible)
                AppWindow.Hide();
            return;
        }
        if (_windowMode)
        {
            PlaceWindow();
            return;
        }
        if (_game == 0 || !_hasItem || !Native.TryGetClientOrigin(_game, out var x, out var y))
            return;

        var scale = Native.GetDpiForWindow(_game) / 96.0;
        // Never taller than the game: the card scrolls instead of running off the screen (its trade buttons stay under it).
        if (Panel.Child is FrameworkElement shown && Native.TryGetClientSize(_game, out _, out var gameHeight))
            shown.MaxHeight = Math.Max(200, (gameHeight - 2 * Margin * scale) / scale);
        Panel.Measure(new Windows.Foundation.Size(Panel.Width, double.PositiveInfinity));
        var size = Panel.DesiredSize;
        int w = (int)Math.Ceiling(size.Width * scale), h = (int)Math.Ceiling(size.Height * scale), margin = (int)(Margin * scale);
        var offset = _offset ?? Beside(_tooltip, w, h, margin) ?? new PointInt32(margin, margin);
        AppWindow.MoveAndResize(new RectInt32(x + offset.X, y + offset.Y, w, h));
        if (!AppWindow.IsVisible)
        {
            AppWindow.Show(false); // show without taking focus from the game
            Telemetry.Track("card_shown");
        }
    }

    /// <summary>
    /// The card in a window of its own: sized to the card (it scrolls beyond the screen's height), at the top right of the
    /// main screen the first time, then wherever the player moves it. Shown without taking the focus.
    /// </summary>
    private void PlaceWindow()
    {
        if (!_hasItem)
            return;
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = Native.GetDpiForWindow(_hwnd) / 96.0;
        if (Panel.Child is FrameworkElement shown)
            shown.MaxHeight = Math.Max(200, (display.Height - 2 * Margin * scale) / scale - 40); // room for the title bar
        Panel.Measure(new Windows.Foundation.Size(Panel.Width, double.PositiveInfinity));
        var size = Panel.DesiredSize;
        AppWindow.ResizeClient(new SizeInt32((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)));
        if (!_windowPlaced)
        {
            var margin = (int)(Margin * scale);
            AppWindow.Move(new PointInt32(display.X + display.Width - AppWindow.Size.Width - margin, display.Y + margin));
            _windowPlaced = true;
        }
        if (!AppWindow.IsVisible)
        {
            AppWindow.Show(false); // the game keeps the focus
            Telemetry.Track("card_shown");
        }
    }

    /// <summary>Sets the window up for where the card shows: over the game, or as an ordinary window (<see cref="AppSettings.ItemCard"/>).</summary>
    private void ApplyMode()
    {
        var window = AppSettings.Shared.ItemCard == CardPlace.Window;
        if (window == _windowMode)
            return;
        if (AppWindow.IsVisible)
            AppWindow.Hide(); // the taskbar notices the window's new style when it's shown again
        GiveFocusBack();
        _windowMode = window;
        _presenter.IsAlwaysOnTop = !window;
        _presenter.SetBorderAndTitleBar(window, window);
        AppWindow.IsShownInSwitchers = window;
        Native.SetToolWindow(_hwnd, !window);
        Native.SetActivatable(_hwnd, window);
        _visibility.Card = window ? 0 : _hwnd; // only the overlay counts as the game while it has the focus
        (_closed, _offset, _windowPlaced) = (false, null, false);
    }

    /// <summary>Closes the card's window for good (Deckard is quitting).</summary>
    public void Shutdown()
    {
        _shuttingDown = true;
        Close();
    }

    // The game reads the mouse as raw input while it's the active window, so a click, drag or wheel on a no-activate card
    // reached the game too. So the card takes the focus while the pointer is over it, and hands it back when the pointer
    // leaves or the card hides. Only window activation: nothing is sent to the game.
    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_windowMode)
            return; // an ordinary window: a click activates it
        Native.SetActivatable(_hwnd, true);
        Native.SetForegroundWindow(_hwnd);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is null && !_editing) // while a value is being corrected the card keeps the keyboard
            GiveFocusBack(); // (nothing to give back in its own window)
    }

    /// <summary>
    /// A value's boxes opened or closed. While open the card is the active window and stays up whatever the overlay key
    /// does, so what's typed goes to the box; closing hands the keyboard back to the game. Only window activation:
    /// nothing is sent to the game.
    /// </summary>
    private void Editing(bool on)
    {
        _editing = on;
        // With the overlay key held rather than toggled, letting go of it to type would hide the card: once a correction
        // starts, the card stays up until the player closes it.
        if (on && !AppSettings.Shared.ToggleOverlay)
            _pinned = true;
        _visibility.Hold = _editing || _pinned;
        if (on)
        {
            Native.SetActivatable(_hwnd, true);
            Native.SetForegroundWindow(_hwnd);
        }
        else if (!_windowMode && Native.GetForegroundWindow() == _hwnd && _game != 0)
            Native.SetForegroundWindow(_game); // still activatable: the pointer is on the card, for the next click
        DispatcherQueue.TryEnqueue(Place); // the card grew or shrank
    }

    /// <summary>A corrected item: kept, and shown again with its build fit; the Trade tab and the stash session get it too.</summary>
    private void Correct(ItemTooltip item)
    {
        if (_catalog is not null)
            _feed.Correct(item, _catalog);
    }

    private void GiveFocusBack()
    {
        if (_windowMode)
            return; // an ordinary window keeps the focus it was given
        if (Native.GetForegroundWindow() == _hwnd && _game != 0)
            Native.SetForegroundWindow(_game);
        Native.SetActivatable(_hwnd, false);
    }

    // Dragging uses screen coordinates from the cursor, so the window moving under the pointer doesn't feed back.
    private void OnDragStart(object sender, PointerRoutedEventArgs e)
    {
        if (_windowMode || !Native.TryGetCursor(out var cursor)) // its own window moves by its title bar
            return;
        _drag = (cursor, AppWindow.Position);
        Panel.CapturePointer(e.Pointer);
    }

    private void OnDragMove(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag || !Native.TryGetCursor(out var cursor))
            return;
        AppWindow.Move(new PointInt32(drag.Window.X + cursor.X - drag.Cursor.X, drag.Window.Y + cursor.Y - drag.Cursor.Y));
    }

    private void OnDragEnd(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag)
            return;
        _drag = null;
        Panel.ReleasePointerCaptures();
        // Only a real drag places the card for good: a click on it once pinned it where it was, so it stopped following
        // the tooltips.
        var moved = Math.Abs(AppWindow.Position.X - drag.Window.X) + Math.Abs(AppWindow.Position.Y - drag.Window.Y);
        if (moved > 4 && _game != 0 && Native.TryGetClientOrigin(_game, out var x, out var y))
            _offset = new PointInt32(AppWindow.Position.X - x, AppWindow.Position.Y - y);
        // A drag that ended off the card (the pointer outran it) got no exit while captured.
        if (Native.TryGetCursor(out var cursor) && (cursor.X < AppWindow.Position.X || cursor.Y < AppWindow.Position.Y
            || cursor.X >= AppWindow.Position.X + AppWindow.Size.Width || cursor.Y >= AppWindow.Position.Y + AppWindow.Size.Height))
            GiveFocusBack();
    }

    /// <summary>The game's tooltips the card is about (a comparison's pair), in game client pixels; null once another tooltip replaced them.</summary>
    public Deckard.Core.Capture.PixelRect? TooltipArea => _hasItem ? _tooltip : null;

    /// <summary>The card is over the game, as Windows has it (in its own window it covers nothing of the game's).</summary>
    public bool Showing => !_windowMode && Native.IsWindowVisible(_hwnd);

    private void CloseCard()
    {
        _pinned = false;
        _visibility.Hold = false; // Place ends a correction still open
        _closed = true;
        Place();
    }

    /// <summary>
    /// A rune's card: its name, Ritual or Invocation, what it does and its Offering (the season data's text), how many the
    /// player has, and that it's saved to their Deckard runes, or updated, or couldn't be read.
    /// </summary>
    private Border RuneCard(ItemFeed.RuneSighting seen)
    {
        var rune = seen.Rune;
        var name = rune?.Name ?? (string.IsNullOrWhiteSpace(seen.Tooltip.RawName) ? "Rune" : seen.Tooltip.RawName!);
        var saved = rune is null ? "Couldn't tell which rune this is: hover it again."
            : seen.Before is null ? $"Saved to your Deckard runes: {rune.Name} ×{seen.Count}"
            : seen.Before != seen.Count ? $"Updated in your Deckard runes: {rune.Name} {seen.Before} → {seen.Count}"
            : $"In your Deckard runes: {rune.Name} ×{seen.Count}";
        var footer = new TextBlock
        {
            Text = saved, FontFamily = ItemCard.Font, FontSize = 14, TextWrapping = TextWrapping.Wrap,
            Foreground = rune is null ? ItemCard.Bad : ItemCard.Good,
        };
        return ItemCard.Build(name, seen.Tooltip.Rarity, seen.Tooltip.TypeLine ?? "Rune", rune is null ? null : $"You have {seen.Count}",
            (rune?.Lines ?? []).Select(l => new CardLine("◆", ItemCard.Bullet, l)), [], 0, footer, CloseCard);
    }

    /// <summary>The read item as a card: its lines with the values read, powers below, then the build comparison.</summary>
    private Border Card(ItemTooltip item, ItemComparison? fit)
    {
        var affixes = new List<CardLine>();
        var powers = new List<CardLine>();
        for (var i = 0; i < item.Lines.Count; i++)
        {
            var (line, index) = (item.Lines[i], i);
            if (line.Matches is not [var best, ..])
            {
                affixes.Add(new CardLine("◆", ItemCard.Bullet, line.Text, Dim: true)); // not a known affix: what OCR read
                continue;
            }
            var text = Fill(best.Entry.Lines[best.LineIndex], best.Values);
            // A line with values corrects on a click; one that looks misread, or was corrected, says so.
            Func<FrameworkElement, FrameworkElement>? wrap = best.Values.Count == 0 ? null : shown => ValueEditor.Wrap(shown, best.Values,
                _catalog is null ? [] : ReadCheck.Check(item, line, _catalog), line.ReadValues,
                values => Correct(ReadCorrections.Entered(item, index, values)), () => Correct(ReadCorrections.Reverted(item, index)), Editing);
            switch (best.Entry.Kind)
            {
                case AffixKind.Aspect or AffixKind.Unique or AffixKind.Mythic:
                    powers.Add(new CardLine("◆", ItemCard.Bullet, text, Wrap: wrap));
                    break;
                case AffixKind.Tempered:
                    affixes.Add(new CardLine("◈", ItemCard.Tempered, text, ItemCard.Tempered, Wrap: wrap));
                    break;
                default:
                    affixes.Add(line.Greater
                        ? new CardLine("✦", ItemCard.Greater, text, ItemCard.Greater, Wrap: wrap)
                        : new CardLine(best.Entry.Kind == AffixKind.Implicit ? "◇" : "◆", ItemCard.Bullet, text, Wrap: wrap));
                    break;
            }
        }

        var info = new List<string>();
        if (item.ItemPower is { } power) info.Add($"{power} Item Power");
        if (item.RequiredLevel is { } level) info.Add($"Requires Level {level}");
        if (item.AccountBound) info.Add("Account bound"); // up here: why there's no trade row, without scrolling the card
        if (item.DamagePerSecond is { } dps) info.Add($"{dps:N0} DPS");
        return ItemCard.Build(
            item.Name ?? "(name not read)", item.Rarity, item.TypeLine ?? item.Rarity ?? "",
            info.Count > 0 ? string.Join(" · ", info) : null, affixes, powers, item.EmptySockets,
            Footer(item, fit), CloseCard,
            // The item power corrects on a click on its line too.
            wrapInfo: item.ItemPower is not { } read ? null : shown => ValueEditor.Wrap(shown, [read], [], item.ItemPowerEdited ? [item.ReadItemPower ?? 0] : null,
                values => Correct(ReadCorrections.WithItemPower(item, (int)values[0])), () => Correct(ReadCorrections.WithItemPower(item, item.ReadItemPower)), Editing));
    }

    /// <summary>
    /// The build comparison, then the trade row: a price check of items like this one, and a button that opens the
    /// item's listing in Deckard (listing stays in Deckard, never the game).
    /// </summary>
    private StackPanel Footer(ItemTooltip item, ItemComparison? fit)
    {
        _actions = null;
        var footer = new StackPanel { Spacing = 10 };
        if (fit is not null)
            footer.Children.Add(Diff(item, fit));
        // Empty sockets: what the guide puts there, and a runeword from the guide or the player's own runes.
        if (_catalog is not null && Runewords.Advise(item, fit?.Planned, _feed.Plan, _feed.OwnedRunes, _catalog) is { Count: > 0 } advice)
        {
            var sockets = new StackPanel { Spacing = 4 };
            sockets.Children.Add(new TextBlock { Text = "SOCKETS", FontFamily = ItemCard.BoldFont, FontSize = 13, Foreground = ItemCard.Number });
            foreach (var line in advice)
                sockets.Children.Add(ItemCard.Line(new CardLine("◯", ItemCard.Bullet, line)));
            footer.Children.Add(sockets);
        }
        if (item.Rarity == "Common" || item.AccountBound)
        {
            // A common (white) item has no affixes and no market, an account-bound one can't be traded: no trade row,
            // and no empty band without a comparison.
            footer.Visibility = footer.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            return footer;
        }
        var list = new Button { Content = "List for trade", FontFamily = ItemCard.Font, Padding = new Thickness(12, 4, 12, 4) };
        list.Click += (_, _) => ListRequested?.Invoke();
        var check = new Button { Content = "Price check", FontFamily = ItemCard.Font, Padding = new Thickness(12, 4, 12, 4) };
        var prices = new TextBlock { FontFamily = ItemCard.Font, FontSize = 13, Foreground = ItemCard.Text, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            prices.Text = await PriceCheckText(item, _catalog?.Season ?? 0);
            prices.Visibility = Visibility.Visible;
            check.IsEnabled = true;
            Place(); // the card grew
        };
        if (item.Truncated)
            footer.Children.Add(Note("Part of this tooltip is scrolled off in game: scroll it there, and the rest joins this card."));
        if (item.AccountBound)
        {
            footer.Children.Add(Note("Account bound: this item can't be traded."));
            return footer; // nothing to price or list
        }
        var buyers = new Button { Content = "Find buyers", FontFamily = ItemCard.Font, Padding = new Thickness(12, 4, 12, 4) };
        var orders = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };
        buyers.Click += async (_, _) =>
        {
            buyers.IsEnabled = false;
            await ShowBuyers(item, _catalog?.Season ?? 0, orders, Place, Caution(item, _catalog));
            orders.Visibility = Visibility.Visible;
            buyers.IsEnabled = true;
            Place(); // the card grew
        };
        var queue = new Button { Content = "Queue", FontFamily = ItemCard.Font, Padding = new Thickness(12, 4, 12, 4) };
        ToolTipService.SetToolTip(queue, "Add it to the items to list in Deckard, to price and list them all at once");
        var caution = Caution(item, _catalog);
        queue.Click += (_, _) =>
        {
            // Never to the market on values the player hasn't looked at: the first click says which, the second queues.
            prices.Text = caution ?? QueueForListing?.Invoke(item) ?? "";
            caution = null;
            prices.Visibility = Visibility.Visible;
            Place(); // the card grew
        };
        // Below the card's scrolling part (see ItemRead): on a card taller than the screen they stay in view.
        _actions = new StackPanel { Spacing = 10, Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { check, buyers, queue, list } }, prices, orders } };
        return footer;

        static TextBlock Note(string text) =>
            new() { Text = text, FontFamily = ItemCard.Font, FontSize = 13, Foreground = ItemCard.Bullet, TextWrapping = TextWrapping.Wrap };
    }

    /// <summary>
    /// What items like this one go for on the trade realm chosen in the settings: the same item type, rarity and aspect
    /// or unique power, and up to three of its affixes (greater ones only against greater ones).
    /// </summary>
    /// <remarks>Also the Stash page's price check.</remarks>
    internal static async Task<string> PriceCheckText(ItemTooltip item, int season)
    {
        if (!TradeClient.Shared.SignedIn)
            return "Sign in to check prices.";
        var realm = AppSettings.Shared.TradeRealm(season);
        if (Deckard.Perception.Trade.TooltipListing.From(item, realm, null) is not { } listing)
            return "Deckard didn't read this item well enough to price it.";
        var search = Deckard.Perception.Trade.TooltipListing.Similar(listing);
        try
        {
            return PriceText(await TradeClient.Shared.PriceCheckAsync(search));
        }
        catch (Exception e) when (e is TradeException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            return e is TradeException ? e.Message : "Can't reach the trade server.";
        }
    }

    /// <summary>
    /// What to say before an item goes to the market with values the player corrected or that look misread: a listing
    /// never goes out on such values unseen. Null when it has neither.
    /// </summary>
    internal static string? Caution(ItemTooltip item, AffixCatalog? catalog)
    {
        var edited = item.Lines.Count(l => l.ReadValues is not null) + (item.ItemPowerEdited ? 1 : 0);
        var flagged = catalog is null ? 0 : item.Lines.Sum(l => ReadCheck.Check(item, l, catalog).Count);
        if (edited + flagged == 0)
            return null;
        static string Values(int n) => n == 1 ? "1 value" : $"{n} values";
        var what = string.Join(" and ", new[] { edited > 0 ? $"{Values(edited)} you corrected" : null, flagged > 0 ? $"{Values(flagged)} that look{(flagged == 1 ? "s" : "")} misread (⚠)" : null }.OfType<string>());
        return $"This item has {what}. Check the card's values, then click again to go on with them.";
    }

    /// <summary>
    /// The buy orders the item fits, best paying first, each with a Sell button: selling makes the deal at once, at the
    /// order's price, and the BattleTags show in Deckard's Offers.
    /// </summary>
    /// <param name="caution">Said on the first click on Sell instead of selling (see <see cref="Caution"/>); the next click sells.</param>
    internal static async Task ShowBuyers(ItemTooltip item, int season, StackPanel orders, Action placed, string? caution = null)
    {
        orders.Children.Clear();
        void Say(string text) => orders.Children.Add(new TextBlock { Text = text, FontFamily = ItemCard.Font, FontSize = 13, Foreground = ItemCard.Text, TextWrapping = TextWrapping.Wrap });
        if (!TradeClient.Shared.SignedIn)
        {
            Say("Sign in to find buyers.");
            return;
        }
        if (Deckard.Perception.Trade.TooltipListing.From(item, AppSettings.Shared.TradeRealm(season), null) is not { } listing)
        {
            Say("Deckard didn't read this item well enough to sell it.");
            return;
        }
        try
        {
            var wanted = await TradeClient.Shared.MatchWantedAsync(listing);
            if (wanted.Count == 0)
                Say("No buy orders fit this item.");
            foreach (var order in wanted)
            {
                var sell = new Button { Content = $"Sell for {Deckard.Core.Trade.Gold.Format(order.PriceGold)}", FontFamily = ItemCard.Font, Padding = new Thickness(12, 4, 12, 4) };
                sell.Click += async (_, _) =>
                {
                    if (caution is not null)
                    {
                        Say(caution);
                        caution = null;
                        placed();
                        return;
                    }
                    sell.IsEnabled = false;
                    try
                    {
                        var deal = await TradeClient.Shared.FillWantedAsync(order.Id, listing);
                        orders.Children.Clear();
                        Say($"Sold for {Deckard.Core.Trade.Gold.Format(deal.PriceGold)} gold to {deal.Counterpart}. Add each other in game; the deal is in Deckard's Offers.");
                    }
                    catch (Exception e) when (e is TradeException or System.Net.Http.HttpRequestException or TaskCanceledException)
                    {
                        Say(e is TradeException ? e.Message : "Can't reach the trade server.");
                    }
                    placed();
                };
                orders.Children.Add(sell);
            }
        }
        catch (Exception e) when (e is TradeException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            Say(e is TradeException ? e.Message : "Can't reach the trade server.");
        }
    }

    /// <summary>"12 listed: 8M to 45M, median 20M. Sold lately: median 18M (5 sales)."</summary>
    internal static string PriceText(Deckard.Core.Trade.PriceCheck prices)
    {
        static string Gold(long? gold) => Deckard.Core.Trade.Gold.Format(gold ?? 0);
        var listed = prices.Listed == 0
            ? "No similar listings."
            : $"{prices.Listed} listed: {Gold(prices.LowGold)} to {Gold(prices.HighGold)}, median {Gold(prices.MedianGold)}.";
        var sold = prices.Sold == 0 ? "" : $" Sold lately: median {Gold(prices.SoldMedianGold)} ({prices.Sold} sale{(prices.Sold == 1 ? "" : "s")}).";
        return listed + sold;
    }

    /// <summary>
    /// How the item compares to the build's item for its slot: what to do with it (keep, craft, salvage, or replace for
    /// worn gear) and why, the
    /// score, each planned affix it has (✓) or lacks (✗) with the build's value, the aspect or unique, and affixes the
    /// build doesn't ask for.
    /// </summary>
    private StackPanel Diff(ItemTooltip item, ItemComparison fit)
    {
        var panel = new StackPanel { Spacing = 5 };
        var scoreBrush = fit.Score >= 0.8 ? ItemCard.Good : fit.Score >= 0.5 ? ItemCard.Fair : ItemCard.Bad;
        if (fit.Verdict is var (verdict, reason))
        {
            var brush = verdict switch { ItemVerdict.Keep => ItemCard.Good, ItemVerdict.Craft => ItemCard.Fair, _ => ItemCard.Bad };
            // Gear the player wears isn't salvaged but replaced, by the build's item for the slot.
            var replace = verdict == ItemVerdict.Salvage && item.IsEquipped;
            panel.Children.Add(new TextBlock
            {
                Text = replace ? "REPLACE" : verdict.ToString().ToUpperInvariant(), Foreground = brush, FontSize = 18, CharacterSpacing = 80, FontFamily = ItemCard.BoldFont,
            });
            if (replace)
                reason += $" Replace it with {fit.PlannedName}.";
            panel.Children.Add(new TextBlock { Text = reason, Foreground = ItemCard.Number, FontSize = 14, TextWrapping = TextWrapping.Wrap, FontFamily = ItemCard.Font });
            scoreBrush = ItemCard.Bullet; // the verdict carries the color; the score is detail
        }
        panel.Children.Add(new TextBlock
        {
            Text = $"Build fit {fit.Score:P0} · vs {fit.PlannedName}", Foreground = scoreBrush, FontSize = 14,
            FontFamily = ItemCard.BoldFont, TextWrapping = TextWrapping.Wrap,
        });

        if (fit.SameUnique == false)
            panel.Children.Add(ItemCard.Line(new CardLine("✗", ItemCard.Bad, $"A different item: the build uses {fit.PlannedName}", ItemCard.Bad)));
        foreach (var entry in fit.Found)
            panel.Children.Add(ItemCard.Line(new CardLine("✓", ItemCard.Good, Planned(fit, entry))));
        foreach (var entry in fit.Missing)
            panel.Children.Add(ItemCard.Line(new CardLine("✗", ItemCard.Bad, Planned(fit, entry))));
        if (fit.AspectMatches is { } aspect)
        {
            var name = fit.Planned.Aspects.Select(a => _catalog?.FindById(a.Id)?.Name).FirstOrDefault(n => n is not null) ?? "the build's aspect";
            panel.Children.Add(ItemCard.Line(aspect
                ? new CardLine("✓", ItemCard.Good, $"Has {name}")
                : new CardLine("✗", ItemCard.Bad, $"Missing {name}", ItemCard.Bad)));
        }

        // Affixes on the item that the build doesn't plan: candidates to reroll at the enchanter.
        var extras = item.Lines
            .Where(l => l.Matches is [{ Entry.Kind: AffixKind.Affix }, ..] && !fit.Found.Contains(l.Matches[0].Entry))
            .Select(l => Fill(l.Matches[0].Entry.Lines[l.Matches[0].LineIndex], l.Matches[0].Values))
            .ToList();
        if (extras.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Not in the build", Foreground = ItemCard.Bullet, FontSize = 13, Margin = new Thickness(0, 4, 0, 0), FontFamily = ItemCard.Font });
            foreach (var extra in extras)
                panel.Children.Add(ItemCard.Line(new CardLine("○", ItemCard.Bullet, extra, ItemCard.Bullet)));
        }
        return panel;
    }

    /// <summary>A planned affix as the build has it, with the planner's value when the plan's affix is found.</summary>
    private string Planned(ItemComparison fit, AffixEntry entry)
    {
        var planned = fit.Planned.Affixes.Concat(fit.Planned.Tempered).FirstOrDefault(a => entry.Ids.Contains(a.Id));
        return planned is not null && _catalog?.AffixText.GetValueOrDefault(planned.Id) is { } text
            ? PlannedItemTooltip.Text(planned, text)
            : string.Join(" / ", entry.Lines);
    }

    /// <summary>Puts the read values back into a template: "+# Maximum Life" with [1195] gives "+1,195 Maximum Life".</summary>
    internal static string Fill(string template, IReadOnlyList<double> values)
    {
        var sb = new StringBuilder();
        var next = 0;
        foreach (var c in template)
        {
            if (c == '#' && next < values.Count)
                sb.Append(values[next++].ToString(values[next - 1] % 1 == 0 ? "N0" : "0.##", CultureInfo.CurrentCulture));
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    internal static partial class Native
    {
        private const int GwlExStyle = -20;
        private const nint WsExLayered = 0x80000, WsExToolWindow = 0x80, WsExNoActivate = 0x8000000;
        private const uint LwaAlpha = 0x2;

        /// <summary>No-activate keeps focus in the game when the card appears; <see cref="SetActivatable"/> lifts it while the pointer is on the card.</summary>
        public static void MakeOverlay(nint hwnd)
        {
            var style = GetWindowLongPtrW(hwnd, GwlExStyle);
            SetWindowLongPtrW(hwnd, GwlExStyle, style | WsExLayered | WsExToolWindow | WsExNoActivate);
            SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha); // a layered window stays invisible until its alpha is set
        }

        /// <summary>A tool window stays out of the taskbar; the card's own window wants to be in it.</summary>
        public static void SetToolWindow(nint hwnd, bool tool)
        {
            var style = GetWindowLongPtrW(hwnd, GwlExStyle);
            SetWindowLongPtrW(hwnd, GwlExStyle, tool ? style | WsExToolWindow : style & ~WsExToolWindow);
        }

        public static void SetActivatable(nint hwnd, bool activatable)
        {
            var style = GetWindowLongPtrW(hwnd, GwlExStyle);
            SetWindowLongPtrW(hwnd, GwlExStyle, activatable ? style & ~WsExNoActivate : style | WsExNoActivate);
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetForegroundWindow(nint hwnd);

        public static bool TryGetClientOrigin(nint hwnd, out int x, out int y)
        {
            var point = new Point();
            var ok = ClientToScreen(hwnd, ref point);
            (x, y) = (point.X, point.Y);
            return ok;
        }

        public static bool TryGetCursor(out PointInt32 cursor)
        {
            var ok = GetCursorPos(out var point);
            cursor = new PointInt32(point.X, point.Y);
            return ok;
        }

        public static bool TryGetClientSize(nint hwnd, out int width, out int height)
        {
            var ok = GetClientRect(hwnd, out var rect);
            (width, height) = (rect.Right - rect.Left, rect.Bottom - rect.Top);
            return ok;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetClientRect(nint hwnd, out Rect rect);

        [LibraryImport("user32.dll")]
        public static partial nint GetForegroundWindow();

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool IsWindowVisible(nint hwnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [LibraryImport("user32.dll")]
        private static partial nint GetWindowLongPtrW(nint hwnd, int index);

        [LibraryImport("user32.dll")]
        private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ClientToScreen(nint hwnd, ref Point point);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetCursorPos(out Point point);

        [LibraryImport("user32.dll")]
        public static partial uint GetDpiForWindow(nint hwnd);
    }
}
