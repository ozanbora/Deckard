using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Deckard.App;

/// <summary>
/// Where Deckard opens until the player is signed in: Battle.net in the browser, or development sign-in against a
/// server on this machine. Raises <see cref="SignedIn"/> once a session exists; closing it without one quits Deckard.
/// </summary>
public sealed partial class SignInWindow : Window
{
    private const string Intro = "Sign in with your Battle.net account. Deckard loads its game data and trades under it.";
    private CancellationTokenSource? _waiting;

    public event Action? SignedIn;

    public SignInWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 178, 172, 162);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;

        // 460 x 600 at the window's scale, in the middle of its screen.
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var size = new Windows.Graphics.SizeInt32((int)(460 * scale), (int)(600 * scale));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2, size.Width, size.Height));

        Message.Text = Intro;
        Error.Foreground = ItemCard.Bad;
        DevPanel.Visibility = TradeClient.Shared.IsDevelopmentServer ? Visibility.Visible : Visibility.Collapsed;
        Closed += (_, _) =>
        {
            _waiting?.Cancel();
            if (!TradeClient.Shared.SignedIn)
                Application.Current.Exit(); // nothing runs without a session
        };
    }

    private async void OnBattleNetClick(object sender, RoutedEventArgs e)
    {
        _waiting = new CancellationTokenSource();
        Waiting.Visibility = Visibility.Visible;
        await AttemptAsync(() => TradeClient.Shared.SignInWithBattleNetAsync(_waiting.Token));
        Waiting.Visibility = Visibility.Collapsed;
        _waiting.Dispose();
        _waiting = null;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _waiting?.Cancel();

    private async void OnDevClick(object sender, RoutedEventArgs e) => await AttemptAsync(() => TradeClient.Shared.SignInAsync(DevTag.Text.Trim()));

    private async void OnDevKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && DevButton.IsEnabled)
            await AttemptAsync(() => TradeClient.Shared.SignInAsync(DevTag.Text.Trim()));
    }

    private async Task AttemptAsync(Func<Task> signIn)
    {
        Error.Visibility = Visibility.Collapsed;
        BattleNetButton.IsEnabled = DevButton.IsEnabled = false;
        try
        {
            await signIn();
            SignedIn?.Invoke();
        }
        catch (Exception e) when (e is TradeException or HttpRequestException or OperationCanceledException
            or System.Net.Sockets.SocketException or System.ComponentModel.Win32Exception)
        {
            Error.Text = e switch
            {
                TradeException => e.Message,
                OperationCanceledException => "Sign-in stopped.",
                System.ComponentModel.Win32Exception => "Deckard couldn't open your browser.",
                _ => "The Deckard server can't be reached.",
            };
            Error.Visibility = Visibility.Visible;
        }
        finally
        {
            BattleNetButton.IsEnabled = DevButton.IsEnabled = true;
        }
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
}
