using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Deckard.App;

public sealed record NoticeAction(string Label, Func<Task> Run);

/// <summary>Something to tell the player about a trade, with buttons to act on it.</summary>
/// <param name="Item">The listed item as the Offers page draws it, so the player knows which listing it's about.</param>
/// <param name="BodyBrush">Colours the body line, e.g. red for an offer below the asking price; null for plain text.</param>
public sealed record TradeNotice(string Title, UIElement Item, string Body, IReadOnlyList<NoticeAction> Actions, Brush? BodyBrush = null);

/// <summary>
/// Trade notices on the right edge of the game while it's in front: a new offer to accept or decline, or an accepted
/// offer with the seller's BattleTag to copy. Like <see cref="OverlayWindow"/> it is our own topmost, no-activate
/// window, so clicking a notice never takes focus from the game, and nothing is sent to the game. One notice shows at a
/// time, the oldest, with how many more wait; the next comes up when it's closed, acted on or expires. A notice that arrives
/// while the game runs in the background waits until the game is back in front; without the game running there are no
/// notices, only the Trade tab's Offers page.
/// </summary>
public sealed partial class TradeToastWindow : Window
{
    private const int Margin = 24;
    private const double CardWidth = 340;
    // One notice at a time, the oldest, so they don't pile up over the game's panels; "+2 more" says others wait.
    private readonly StackPanel _stack = new() { Width = CardWidth };
    private readonly TextBlock _more = new() { FontFamily = ItemCard.Font, FontSize = 13, Opacity = 0.75, Margin = new Thickness(14, 0, 10, 8) };
    private readonly StackPanel _window;
    private nint _game;

    public TradeToastWindow(ItemFeed feed)
    {
        Title = "Deckard trade notices";
        _window = new StackPanel { Width = CardWidth, Children = { _stack, _more } };
        Content = new Grid { RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(ColorHelper.FromArgb(255, 14, 13, 12)), Children = { _window } };
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        OverlayWindow.Native.MakeOverlay(WinRT.Interop.WindowNative.GetWindowHandle(this));

        feed.GameWindowChanged += hwnd => DispatcherQueue.TryEnqueue(() =>
        {
            _game = hwnd;
            Place();
        });
        // Follow the game window, and hide while another window is in front.
        var follow = DispatcherQueue.CreateTimer();
        follow.Interval = TimeSpan.FromMilliseconds(500);
        follow.Tick += (_, _) => Place();
        follow.Start();
    }

    private bool GameInFront => _game != 0 && OverlayWindow.Native.GetForegroundWindow() == _game;

    /// <summary>
    /// Whether the item card is up (set by the app): notices step aside while it is, as both open left of a panel on the
    /// right and the notice covered the card's name and verdict. They come back when it closes.
    /// </summary>
    public Func<bool> CardShowing { get; set; } = () => false;

    /// <summary>Where the game's item tooltips last read are (game client pixels), or null (set by the app): notices keep off them.</summary>
    public Func<Deckard.Core.Capture.PixelRect?> TooltipArea { get; set; } = () => null;

    private int _dockedPanelLeft;

    /// <summary>
    /// The left edge (game client pixels) of a game panel open on the right (Character, Abilities), or 0: notices then
    /// stand left of it and of the tooltips the game opens beside it, instead of covering them. Set on the UI thread.
    /// </summary>
    public int DockedPanelLeft
    {
        get => _dockedPanelLeft;
        set
        {
            if (_dockedPanelLeft == value)
                return;
            _dockedPanelLeft = value;
            Place();
        }
    }

    /// <summary>
    /// Shows the notice for the settings' notice duration, counted from when it first appears over the game (an
    /// Alt+Tab when it arrives doesn't make the player miss it). Without the game running, or with trade notices
    /// turned off in the settings, it's dropped: the Offers page has it. A notice that isn't about trading (the
    /// first-time hint) shows whatever the trade notice setting says.
    /// </summary>
    public void Post(TradeNotice notice, bool aboutTrading = true)
    {
        if (_game == 0 || aboutTrading && !AppSettings.Shared.ShowTradeNotices)
            return;
        var card = Card(notice);
        var expiry = DispatcherQueue.CreateTimer();
        expiry.Interval = TimeSpan.FromSeconds(AppSettings.Shared.NoticeSeconds);
        expiry.IsRepeating = false;
        expiry.Tick += (_, _) => Remove(card);
        card.Tag = expiry; // started by Place once the card is on screen
        _stack.Children.Add(card);
        Place();
    }

    /// <summary>Places or hides the notice now, e.g. when the item card appears, rather than at the next half second.</summary>
    public void Refresh() => Place();

    private void Remove(UIElement card)
    {
        _stack.Children.Remove(card);
        Place();
    }

    /// <summary>On the game's right edge, a quarter of the way down, sized to the notices.</summary>
    private void Place()
    {
        if (_game == 0)
            _stack.Children.Clear(); // the game closed: its pending notices go too (the Offers page keeps them)
        if (_stack.Children.Count == 0 || !GameInFront || CardShowing()
            || !OverlayWindow.Native.TryGetClientOrigin(_game, out var x, out var y)
            || !OverlayWindow.Native.TryGetClientSize(_game, out var width, out var height))
        {
            if (AppWindow.IsVisible)
                AppWindow.Hide();
            return;
        }
        // The oldest notice shows; the others wait their turn, their time not yet counting.
        for (var i = 0; i < _stack.Children.Count; i++)
            _stack.Children[i].Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
        var waiting = _stack.Children.Count - 1;
        _more.Text = waiting == 1 ? "+1 more notice" : $"+{waiting} more notices";
        _more.Visibility = waiting > 0 ? Visibility.Visible : Visibility.Collapsed;
        var scale = OverlayWindow.Native.GetDpiForWindow(_game) / 96.0;
        _window.Measure(new Windows.Foundation.Size(CardWidth, double.PositiveInfinity));
        var (w, h) = ((int)Math.Ceiling(CardWidth * scale), (int)Math.Ceiling(_window.DesiredSize.Height * scale));
        // Beside the game's right edge, or left of an open panel and the item and skill tooltips the game opens left of it
        // (measured at 3440x1440: the advanced item tooltip reaches 535 px, 0.37 of the height, out from the panel).
        var right = _dockedPanelLeft is > 0 and var left && left < width ? left - (int)(height * 0.40) : width;
        var left0 = right - w - (int)(Margin * scale);
        // The tooltips last read (a comparison's pair reaches further left than one tooltip): the notice stands left of them.
        if (TooltipArea() is { } area && left0 < area.Right && left0 + w > area.X && height / 4 < area.Bottom && height / 4 + h > area.Y)
            left0 = Math.Max((int)(Margin * scale), area.X - w - (int)(Margin * scale));
        AppWindow.MoveAndResize(new RectInt32(x + left0, y + height / 4, w, h));
        if (!AppWindow.IsVisible)
            AppWindow.Show(false); // without taking focus from the game
        if (_stack.Children[0] is FrameworkElement { Tag: Microsoft.UI.Dispatching.DispatcherQueueTimer { IsRunning: false } expiry })
            expiry.Start();
    }

    private Border Card(TradeNotice notice)
    {
        var panel = new StackPanel { Spacing = 6 };
        var close = new Button { Content = "✕", Padding = new Thickness(6, 2, 6, 2), Background = null, BorderThickness = new Thickness(0) };
        var header = new Grid { ColumnDefinitions = { new(), new() { Width = GridLength.Auto } } };
        header.Children.Add(new TextBlock { Text = notice.Title, FontFamily = ItemCard.BoldFont, FontSize = 16, Foreground = ItemCard.Fair, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        panel.Children.Add(header);
        panel.Children.Add(notice.Item);
        var body = new TextBlock { Text = notice.Body, FontFamily = ItemCard.Font, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        if (notice.BodyBrush is { } brush)
            (body.Foreground, body.Opacity) = (brush, 1);
        panel.Children.Add(body);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(buttons);
        var card = new Border
        {
            Child = panel,
            Padding = new Thickness(14, 10, 10, 12),
            BorderThickness = new Thickness(1),
            BorderBrush = ItemCard.Bullet,
        };
        close.Click += (_, _) => Remove(card);
        foreach (var action in notice.Actions)
        {
            var button = new Button { Content = action.Label };
            button.Click += async (_, _) =>
            {
                Remove(card);
                await action.Run();
            };
            buttons.Children.Add(button);
        }
        return card;
    }
}
