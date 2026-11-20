using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Deckard.Capture;
using Deckard.Core.Capture;
using Frame = Deckard.Core.Capture.Frame;
using Deckard.Perception.Feedback;
using Deckard.Perception.Imaging;
using Deckard.Perception.Ocr;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace Deckard.App;

/// <summary>
/// Send feedback: a bug report or a feature request, with up to 3 screenshots, to the Deckard server, which files it as
/// a GitHub issue (ROADMAP.md). Screenshots are covered where personal data may show before they leave the PC: Deckard
/// suggests boxes (ScreenshotRedaction), the player adds and removes them, and only the covered image is sent.
/// </summary>
internal sealed class FeedbackWindow : Window
{
    private const int MaxShots = 3, MaxPngBytes = 5_000_000;

    private sealed class Shot(Frame frame, bool game) : IDisposable
    {
        public Frame Frame { get; } = frame;
        public bool Game { get; } = game;
        public List<PixelRect> Boxes { get; } = [];
        public ImageSource? Preview { get; set; }
        public void Dispose() => Frame.Dispose();
    }

    private readonly nint _deckard;
    private readonly Func<PixelRect?> _accountBox;
    private readonly List<Shot> _shots = [];
    private Shot? _editing;

    private readonly RadioButtons _kind = new() { Header = "This is", Items = { "A bug report", "A feature request" }, SelectedIndex = 0, MaxColumns = 2 };
    private readonly TextBox _title = new() { Header = "Title", MaxLength = 120 };
    private readonly TextBox _description = new() { Header = "What happened, or what would you like?", MaxLength = 5000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 140 };
    private readonly StackPanel _thumbs = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Grid _editor = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly StackPanel _editorPanel = new() { Spacing = 6, Visibility = Visibility.Collapsed };
    private readonly CheckBox _diagnostics = new() { Content = "Include diagnostics: app version, Windows, screen size, recent errors" };
    private readonly TextBlock _diagnosticsText = new() { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly CheckBox _checked = new() { Content = "I checked the screenshots: nothing private is visible", Visibility = Visibility.Collapsed };
    private readonly Button _send = new() { Content = "Send", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };

    /// <param name="deckard">Deckard's main window, for "Capture Deckard".</param>
    /// <param name="accountBox">Where the main window draws the player's BattleTag (window pixels), covered exactly.</param>
    public FeedbackWindow(nint deckard, Func<PixelRect?> accountBox)
    {
        _deckard = deckard;
        _accountBox = accountBox;
        Title = "Deckard: send feedback";
        ExtendsContentIntoTitleBar = false;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 900));
        AppWindow.SetIcon(App.IconFile);

        var captureGame = new Button { Content = "Capture Diablo IV" };
        var captureDeckard = new Button { Content = "Capture Deckard" };
        var addFile = new Button { Content = "Add a file…" };
        captureGame.Click += async (_, _) => await AddAsync(CaptureAsync(WindowLocator.FindByProcessName(WindowLocator.Diablo4ProcessName), "Diablo IV isn't running."), game: true);
        captureDeckard.Click += async (_, _) => await AddAsync(CaptureAsync(_deckard, "Deckard's window isn't open."), game: false);
        addFile.Click += async (_, _) => await AddAsync(PickFileAsync(), game: false);

        // The editor: the screenshot at its pixel size, scaled to fit; boxes are drawn on the canvas in image pixels.
        _editor.Children.Add(_image);
        _editor.Children.Add(_canvas);
        _canvas.PointerPressed += OnBoxStart;
        _canvas.PointerMoved += OnBoxDrag;
        _canvas.PointerReleased += OnBoxEnd;
        var clear = new Button { Content = "Clear boxes" };
        clear.Click += (_, _) => { _editing?.Boxes.Clear(); Redraw(); };
        var remove = new Button { Content = "Remove this screenshot" };
        remove.Click += (_, _) => Remove(_editing);
        _editorPanel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 13, Opacity = 0.8,
            Text = "Black boxes cover what Deckard thinks may be private (BattleTags, chat, party). Drag over anything else to cover it; "
                + "click a box to uncover it. Only the covered image is sent.",
        });
        _editorPanel.Children.Add(new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["SurfaceBorderBrush"], BorderThickness = new Thickness(1), MaxHeight = 420,
            Child = new Viewbox { Child = _editor, Stretch = Stretch.Uniform },
        });
        _editorPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { clear, remove } });

        _diagnostics.Checked += (_, _) => { _diagnosticsText.Text = Diagnostics(); _diagnosticsText.Visibility = Visibility.Visible; };
        _diagnostics.Unchecked += (_, _) => _diagnosticsText.Visibility = Visibility.Collapsed;
        _send.Click += async (_, _) => await SendAsync();
        _title.TextChanged += (_, _) => UpdateSend();
        _description.TextChanged += (_, _) => UpdateSend();
        _checked.Checked += (_, _) => UpdateSend();
        _checked.Unchecked += (_, _) => UpdateSend();

        var page = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        page.Children.Add(_kind);
        page.Children.Add(_title);
        page.Children.Add(_description);
        page.Children.Add(new TextBlock { Text = $"Screenshots (up to {MaxShots})", Margin = new Thickness(0, 6, 0, 0) });
        page.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { captureGame, captureDeckard, addFile } });
        page.Children.Add(_thumbs);
        page.Children.Add(_editorPanel);
        page.Children.Add(_diagnostics);
        page.Children.Add(_diagnosticsText);
        page.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 13, Opacity = 0.7,
            Text = "Your report becomes a public issue on Deckard's GitHub, without your BattleTag: anyone can read the title, description "
                + "and diagnostics, so leave out personal details. Screenshots stay on Deckard's server, seen only "
                + "by its developer, and are deleted 90 days after the issue is closed.",
        });
        page.Children.Add(_checked);
        page.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _send, _status } });
        Content = new Grid
        {
            Background = (Brush)Application.Current.Resources["AppBackgroundBrush"],
            RequestedTheme = ElementTheme.Dark,
            Children = { new ScrollViewer { Content = page } },
        };
        Closed += (_, _) => _shots.ForEach(s => s.Dispose());
        UpdateSend();
    }

    private async Task<Frame?> CaptureAsync(nint hwnd, string missing)
    {
        if (hwnd == 0)
        {
            _status.Text = missing;
            return null;
        }
        await using var source = await WgcFrameSource.CreateForWindowAsync(hwnd);
        source.Start();
        var frame = await source.Frames.ReadAsync();
        // The first frame can be stale; take a newer one if the window changes within half a second.
        using var wait = new CancellationTokenSource(500);
        try
        {
            var newer = await source.Frames.ReadAsync(wait.Token);
            frame.Dispose();
            frame = newer;
        }
        catch (OperationCanceledException)
        {
        }
        return frame;
    }

    private async Task<Frame?> PickFileAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { FileTypeFilter = { ".png", ".jpg", ".jpeg" } };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return await picker.PickSingleFileAsync() is { } file ? await FrameFile.LoadAsync(file.Path) : null;
    }

    private async Task AddAsync(Task<Frame?> getting, bool game)
    {
        if (_shots.Count >= MaxShots)
        {
            _status.Text = $"Up to {MaxShots} screenshots.";
            return;
        }
        try
        {
            if (await getting is not { } frame)
                return;
            var shot = new Shot(frame, game);
            _status.Text = "Looking for anything private…";
            shot.Boxes.AddRange(await SuggestAsync(shot));
            shot.Preview = await PreviewAsync(frame);
            _shots.Add(shot);
            ShowThumbs();
            Edit(shot);
            _status.Text = "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or COMException)
        {
            _status.Text = $"Couldn't add the screenshot: {e.Message}";
        }
        UpdateSend();
    }

    private async Task<IReadOnlyList<PixelRect>> SuggestAsync(Shot shot)
    {
        IReadOnlyList<TextLine> lines;
        try
        {
            lines = await TextRecognizer.Create().RecognizeAsync(shot.Frame);
        }
        catch (Exception e) when (e is ArgumentException or COMException or InvalidOperationException)
        {
            lines = []; // too large for OCR or no OCR language: only the fixed areas, and the player's own boxes
        }
        var boxes = ScreenshotRedaction.Suggest(lines, shot.Frame.Width, shot.Frame.Height, shot.Game).ToList();
        if (!shot.Game && _accountBox() is { } account)
            boxes.Add(account); // exactly where Deckard draws the player's BattleTag
        return boxes;
    }

    private static async Task<ImageSource> PreviewAsync(Frame frame)
    {
        var png = await FrameFile.EncodePngAsync(frame);
        var image = new BitmapImage();
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        await image.SetSourceAsync(stream);
        return image;
    }

    private void ShowThumbs()
    {
        _thumbs.Children.Clear();
        foreach (var shot in _shots)
        {
            var thumb = new Button { Padding = new Thickness(2), Content = new Image { Source = shot.Preview, Height = 72 } };
            thumb.Click += (_, _) => Edit(shot);
            _thumbs.Children.Add(thumb);
        }
    }

    private void Edit(Shot? shot)
    {
        _editing = shot;
        _editorPanel.Visibility = shot is null ? Visibility.Collapsed : Visibility.Visible;
        if (shot is null)
            return;
        _image.Source = shot.Preview;
        _editor.Width = _canvas.Width = shot.Frame.Width;
        _editor.Height = _canvas.Height = shot.Frame.Height;
        Redraw();
    }

    private void Remove(Shot? shot)
    {
        if (shot is null)
            return;
        _shots.Remove(shot);
        shot.Dispose();
        ShowThumbs();
        Edit(_shots.LastOrDefault());
        UpdateSend();
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        foreach (var box in _editing?.Boxes ?? [])
        {
            var rect = new Rectangle { Width = box.Width, Height = box.Height, Fill = new SolidColorBrush(Microsoft.UI.Colors.Black) };
            Canvas.SetLeft(rect, box.X);
            Canvas.SetTop(rect, box.Y);
            rect.PointerPressed += (_, e) =>
            {
                e.Handled = true; // a click on a box uncovers it rather than starting a new one
                _editing?.Boxes.Remove(box);
                Redraw();
            };
            _canvas.Children.Add(rect);
        }
    }

    // Dragging a new box, in image pixels (the canvas is the image's size inside the Viewbox).
    private Windows.Foundation.Point? _dragFrom;
    private Rectangle? _dragging;

    private void OnBoxStart(object sender, PointerRoutedEventArgs e)
    {
        if (_editing is null)
            return;
        _dragFrom = e.GetCurrentPoint(_canvas).Position;
        _dragging = new Rectangle { Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 0, 0, 0)), Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"], StrokeThickness = 2 };
        _canvas.Children.Add(_dragging);
        _canvas.CapturePointer(e.Pointer);
    }

    private void OnBoxDrag(object sender, PointerRoutedEventArgs e)
    {
        if (_dragFrom is not { } from || _dragging is null)
            return;
        var to = e.GetCurrentPoint(_canvas).Position;
        Canvas.SetLeft(_dragging, Math.Min(from.X, to.X));
        Canvas.SetTop(_dragging, Math.Min(from.Y, to.Y));
        _dragging.Width = Math.Abs(to.X - from.X);
        _dragging.Height = Math.Abs(to.Y - from.Y);
    }

    private void OnBoxEnd(object sender, PointerRoutedEventArgs e)
    {
        if (_dragFrom is not { } from || _editing is null)
            return;
        var to = e.GetCurrentPoint(_canvas).Position;
        _dragFrom = null;
        _dragging = null;
        _canvas.ReleasePointerCapture(e.Pointer);
        var box = new PixelRect((int)Math.Min(from.X, to.X), (int)Math.Min(from.Y, to.Y), (int)Math.Abs(to.X - from.X), (int)Math.Abs(to.Y - from.Y))
            .Intersect(new PixelRect(0, 0, _editing.Frame.Width, _editing.Frame.Height));
        if (box.Width >= 4 && box.Height >= 4)
            _editing.Boxes.Add(box);
        Redraw();
    }

    private void UpdateSend()
    {
        _checked.Visibility = _shots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _send.IsEnabled = _title.Text.Trim().Length > 0 && _description.Text.Trim().Length > 0 && (_shots.Count == 0 || _checked.IsChecked == true);
    }

    /// <summary>What a report's diagnostics hold: no account, no build, the Windows user's folder hidden.</summary>
    private string Diagnostics()
    {
        var area = Microsoft.UI.Windowing.DisplayArea.Primary.OuterBounds;
        var recent = ErrorLog.Recent(6000);
        return $"Deckard {ErrorLog.Version} ({(Supporter.EarlyAccess ? "early" : "normal")} updates)\n"
            + $"Windows {Environment.OSVersion.Version}, display language {System.Globalization.CultureInfo.CurrentUICulture.Name}\n"
            + $"Screen {area.Width}x{area.Height}, scale {Content.XamlRoot?.RasterizationScale * 100 ?? 100:0}%\n"
            + (recent.Length > 0 ? $"Recent errors:\n{recent}" : "No errors logged.");
    }

    private async Task SendAsync()
    {
        _send.IsEnabled = false;
        _status.Text = "Sending…";
        try
        {
            var pngs = new List<byte[]>();
            foreach (var shot in _shots)
                pngs.Add(await CoveredPngAsync(shot));
            var receipt = await TradeClient.Shared.SendFeedbackAsync(_kind.SelectedIndex == 1 ? "feature" : "bug", _title.Text.Trim(),
                _description.Text.Trim(), _diagnostics.IsChecked == true ? Diagnostics() : null, pngs);
            _status.Text = $"Thank you! Sent as report #{receipt.ReportId}.";
            _title.Text = _description.Text = "";
            foreach (var shot in _shots.ToList())
                Remove(shot);
            _checked.IsChecked = false;
        }
        catch (Exception e) when (e is TradeException or HttpRequestException or TaskCanceledException or COMException)
        {
            _status.Text = $"Couldn't send it: {e.Message}";
        }
        UpdateSend();
    }

    /// <summary>The screenshot with its boxes burned in, as PNG, halved until it fits the server's size limit.</summary>
    private static async Task<byte[]> CoveredPngAsync(Shot shot)
    {
        var frame = shot.Frame.Crop(new PixelRect(0, 0, shot.Frame.Width, shot.Frame.Height)); // a copy: the original stays editable
        try
        {
            ScreenshotRedaction.Fill(frame, shot.Boxes);
            var png = await FrameFile.EncodePngAsync(frame);
            while (png.Length > MaxPngBytes && frame.Width > 640)
            {
                var half = ImageOps.Halve(frame);
                frame.Dispose();
                frame = half;
                png = await FrameFile.EncodePngAsync(frame);
            }
            return png;
        }
        finally
        {
            frame.Dispose();
        }
    }
}
