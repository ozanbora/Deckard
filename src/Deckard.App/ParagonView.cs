using Deckard.Core.Builds;
using Deckard.Core.Items;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Deckard.App;

/// <summary>
/// The build's paragon boards laid out like the game's paragon screen: each board a 21x21 grid of nodes, turned and
/// placed so neighbouring boards' gates touch. Nodes are tiles in their rarity's colour with the game's white symbol;
/// the build's nodes and the paths between them are lit, everything else is dimmed. Opens with all boards in view; drag to
/// move, Ctrl+wheel to zoom.
/// </summary>
public sealed partial class ParagonView : UserControl
{
    private const double Cell = 28;   // pixels per grid cell
    private const double Border = 40; // pixels around the boards

    private static readonly SolidColorBrush Gold = Brush(240, 192, 96);
    private static readonly SolidColorBrush GoldLine = Brush(214, 160, 74);
    private static readonly SolidColorBrush Line = Brush(70, 64, 56);
    private static readonly SolidColorBrush BoardFill = Brush(26, 23, 20);
    private static readonly SolidColorBrush BoardEdge = Brush(66, 56, 44);
    private static readonly SolidColorBrush Muted = Brush(150, 142, 130);

    private readonly Canvas _canvas = new();
    private readonly ScrollViewer _scroll;
    private Point? _openAt; // where to open once the view has a size (the tab may be hidden when the build loads)

    public ParagonView()
    {
        _scroll = new ScrollViewer
        {
            Content = _canvas,
            ZoomMode = ZoomMode.Enabled,
            MinZoomFactor = 0.2f,
            MaxZoomFactor = 3,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brush(16, 14, 12),
        };
        Content = ZoomControls.Wrap(_scroll, _canvas);
        _scroll.SizeChanged += (_, _) => OpenAll();
        _canvas.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _ = new DragToPan(_scroll, cursor => ProtectedCursor = cursor);
    }

    /// <summary>Opens with every board in view (as far as the minimum zoom allows), centred.</summary>
    private void OpenAll()
    {
        // The control's own size: when the tab first shows, SizeChanged comes before the viewport has a size.
        if (_openAt is not { } p || _scroll.ActualWidth <= 0)
            return;
        _openAt = null;
        // After the pending layout pass: until the new canvas is measured, ChangeView ignores the zoom.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            double width = _scroll.ActualWidth, height = _scroll.ActualHeight;
            var zoom = (float)Math.Clamp(Math.Min(width / _canvas.Width, height / _canvas.Height), _scroll.MinZoomFactor, 1);
            _scroll.ChangeView(Math.Max(0, p.X * zoom - width / 2), Math.Max(0, p.Y * zoom - height / 2), zoom, true);
        });
    }

    /// <param name="boards">The build's boards (a planner step), or none to show an empty view.</param>
    public void Show(ParagonData paragon, IReadOnlyList<PlacedBoard> boards)
    {
        _canvas.Children.Clear();
        var placed = boards.Where(b => paragon.Boards.ContainsKey(b.Id)).ToList();
        if (placed.Count == 0)
        {
            _canvas.Width = _canvas.Height = 0;
            return;
        }

        int width = paragon.Boards[placed[0].Id].Width;
        int minX = placed.Min(b => b.X) * width, minY = placed.Min(b => b.Y) * width;
        Point At((int X, int Y) cell) => new((cell.X - minX + 0.5) * Cell + Border, (cell.Y - minY + 0.5) * Cell + Border);
        _canvas.Width = (placed.Max(b => b.X) * width + width - minX) * Cell + 2 * Border;
        _canvas.Height = (placed.Max(b => b.Y) * width + width - minY) * Cell + 2 * Border;

        // Every node on the paragon by cell, with its board and whether the build takes it.
        var cells = new Dictionary<(int X, int Y), (ParagonNode Node, PlacedBoard Board, bool Taken)>();
        foreach (var board in placed)
        {
            var grid = paragon.Boards[board.Id].Nodes;
            for (var i = 0; i < grid.Count; i++)
                if (grid[i] is { } key && paragon.Nodes.TryGetValue(key, out var node))
                    cells[board.Cell(i, width)] = (node, board, board.Nodes.Contains(i));
        }

        foreach (var board in placed)
        {
            var corner = At((board.X * width, board.Y * width));
            var frame = new Rectangle { Width = width * Cell, Height = width * Cell, Fill = BoardFill, Stroke = BoardEdge, StrokeThickness = 2, RadiusX = 8, RadiusY = 8 };
            Canvas.SetLeft(frame, corner.X - Cell / 2);
            Canvas.SetTop(frame, corner.Y - Cell / 2);
            _canvas.Children.Add(frame);
        }

        // Links between neighbouring nodes: lit where the build's path runs.
        foreach (var (cell, (_, _, taken)) in cells)
            foreach (var next in new[] { (cell.X + 1, cell.Y), (cell.X, cell.Y + 1) })
                if (cells.TryGetValue(next, out var other))
                {
                    var lit = taken && other.Taken;
                    var (a, b) = (At(cell), At(next));
                    _canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = lit ? GoldLine : Line, StrokeThickness = lit ? 3 : 1.5 });
                }

        foreach (var (cell, (node, board, taken)) in cells)
            _canvas.Children.Add(NodeElement(node, taken, board.Glyph is { } glyph && node.Kind == "socket" ? paragon.Glyphs.GetValueOrDefault(glyph) : null, board, At(cell)));

        // Board names (and their glyphs) along each board's top edge, on top of everything.
        foreach (var board in placed)
        {
            var corner = At((board.X * width, board.Y * width));
            var glyph = board.Glyph is { } g && paragon.Glyphs.TryGetValue(g, out var info) ? $"  ·  {info.Name}" + (board.GlyphLevel > 0 ? $" (level {board.GlyphLevel})" : "") : "";
            var label = new TextBlock
            {
                Text = paragon.Boards[board.Id].Name + glyph,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Gold,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(label, corner.X);
            Canvas.SetTop(label, corner.Y - Cell / 2 + 4);
            _canvas.Children.Add(label);
        }

        _openAt = new Point(_canvas.Width / 2, _canvas.Height / 2);
        OpenAll();
    }

    /// <summary>A node as a tile in its rarity's colour: square for stats, a diamond for gates, round for sockets and the start.</summary>
    private static FrameworkElement NodeElement(ParagonNode node, bool taken, ParagonGlyph? glyph, PlacedBoard board, Point p)
    {
        var (size, fill) = node.Kind switch
        {
            "magic" => (20.0, Brush(46, 84, 160)),
            "rare" => (22.0, Brush(176, 140, 40)),
            "legendary" => (26.0, Brush(200, 92, 30)),
            "gate" => (18.0, Brush(110, 100, 86)),
            "socket" => (26.0, Brush(40, 36, 32)),
            "start" => (22.0, Brush(150, 30, 30)),
            _ => (16.0, Brush(92, 86, 78)),
        };
        var stroke = taken ? Gold : BoardEdge;
        var thickness = taken ? 2.0 : 1.0;
        Shape frame = node.Kind is "socket" or "start"
            ? new Ellipse { Width = size, Height = size, Fill = fill, Stroke = stroke, StrokeThickness = thickness }
            : new Rectangle { Width = size, Height = size, Fill = fill, Stroke = stroke, StrokeThickness = thickness, RadiusX = 3, RadiusY = 3 };
        if (node.Kind == "gate")
        {
            frame.RenderTransformOrigin = new Point(0.5, 0.5);
            frame.RenderTransform = new RotateTransform { Angle = 45 };
        }
        var grid = new Grid { Width = size, Height = size, Children = { frame } };
        if (node.Icon != 0)
            grid.Children.Add(Icon(node.Icon, node.IconIsMask ? size * 0.7 : size));
        // Unused nodes fade back; a socket stays visible when it holds the build's glyph.
        grid.Opacity = taken || glyph is not null ? 1 : 0.35;
        ToolTipService.SetToolTip(grid, Tooltip(node, taken, glyph, board));
        Canvas.SetLeft(grid, p.X - size / 2);
        Canvas.SetTop(grid, p.Y - size / 2);
        return grid;
    }

    /// <summary>The node's tooltip: name, whether the build takes it, then what it grants (or the socketed glyph).</summary>
    private static StackPanel Tooltip(ParagonNode node, bool taken, ParagonGlyph? glyph, PlacedBoard board)
    {
        var panel = new StackPanel { MaxWidth = 380, Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = glyph?.Name ?? node.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Gold, TextWrapping = TextWrapping.Wrap });
        var kind = node.Kind switch { "socket" => "Glyph socket", "gate" => "Board attachment gate", "start" => "Starting node", var k => $"{char.ToUpperInvariant(k[0])}{k[1..]} node" };
        panel.Children.Add(new TextBlock
        {
            Text = glyph is not null ? $"Glyph{(board.GlyphLevel > 0 ? $" · level {board.GlyphLevel}" : "")}" : $"{kind} · {(taken ? "in the build" : "not in the build")}",
            FontSize = 12,
            Opacity = 0.75,
        });
        foreach (var line in glyph?.Lines ?? node.Lines)
        {
            var text = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap };
            if (line.StartsWith("Bonus with", StringComparison.Ordinal))
                text.Foreground = Muted; // a rare node's conditional bonus
            panel.Children.Add(text);
        }
        return panel;
    }

    private static Image Icon(long id, double size)
    {
        var image = new Image { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        _ = LoadAsync(image, id);
        return image;
    }

    private static async Task LoadAsync(Image image, long id) => image.Source = await IconCache.GetAsync(id);

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));
}
