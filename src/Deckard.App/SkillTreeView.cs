using Deckard.Core.Items;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Deckard.App;

/// <summary>
/// The class's skill tree drawn the way the game draws it: red diamond cluster hubs on a thick red trunk, skills as
/// square icons with their rank, modifiers as small diamonds, junctions as dots, all at the game's own layout.
/// The build's nodes are lit with a gold frame and their rank; everything else is dimmed. Scrolls vertically like the
/// in-game tree (opening at the Basic skills at the top); Ctrl+wheel zooms. While <see cref="Editing"/>, a click on a
/// node asks for a point more and a right-click for a point less (<see cref="PointChanged"/>).
/// </summary>
public sealed partial class SkillTreeView : UserControl
{
    private const double PixelsPerUnit = 0.085; // close to the game's scale at 1440p, where skill icons read clearly
    private const double Border = 60;           // pixels around the outermost nodes
    private const double SkillSize = 44, ModifierSize = 22, PassiveSize = 30, ClusterSize = 64, JunctionSize = 9;

    private static readonly SolidColorBrush Gold = Brush(240, 192, 96);
    private static readonly SolidColorBrush GoldLine = Brush(214, 160, 74);
    private static readonly SolidColorBrush Fresh = Brush(110, 200, 110); // points the shown leveling step adds
    private static readonly SolidColorBrush Bronze = Brush(120, 96, 64);
    private static readonly SolidColorBrush Line = Brush(96, 90, 80);
    private static readonly SolidColorBrush TrunkDark = Brush(110, 18, 18);
    private static readonly SolidColorBrush TrunkLight = Brush(196, 40, 40);
    private static readonly SolidColorBrush ClusterFill = Brush(120, 20, 20);
    private static readonly SolidColorBrush NodeFill = Brush(28, 25, 22);
    private static readonly SolidColorBrush Muted = Brush(150, 142, 130);

    private readonly Canvas _canvas = new();
    private readonly ScrollViewer _scroll;
    private Point? _openAt; // where to open once the view has a size (the tab may be hidden when the build loads)

    public SkillTreeView()
    {
        _scroll = new ScrollViewer
        {
            Content = _canvas,
            ZoomMode = ZoomMode.Enabled,
            MinZoomFactor = 0.4f,
            MaxZoomFactor = 3,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brush(16, 14, 12),
        };
        Content = ZoomControls.Wrap(_scroll, _canvas);
        _scroll.SizeChanged += (_, _) => OpenAtBasic();

        // Drag with the left mouse button to move the tree, like in game.
        _canvas.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _ = new DragToPan(_scroll, cursor => ProtectedCursor = cursor);
        // The drag takes the mouse on press, so clicks are found here: a press and release in the same spot, on a node.
        _scroll.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) => _press = e.GetCurrentPoint(_canvas).Position), true);
        _scroll.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnRelease), true);
    }

    /// <summary>Clicks on nodes change points (see <see cref="PointChanged"/>).</summary>
    public bool Editing { get; set; }

    /// <summary>A node clicked while editing: its id, and true for a point more (left click) or false for one less (right click).</summary>
    public event Action<int, bool>? PointChanged;

    private Point? _press;
    private readonly List<(SkillNode Node, Point At, double Radius)> _targets = [];
    private SkillTree? _shown;

    private void OnRelease(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas);
        if (!Editing || _press is not { } press || Math.Abs(point.Position.X - press.X) > 4 || Math.Abs(point.Position.Y - press.Y) > 4)
            return;
        _press = null;
        var kind = point.Properties.PointerUpdateKind;
        if (kind is not (Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased or Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased))
            return;
        var hit = _targets.Where(t => Math.Abs(t.At.X - press.X) <= t.Radius && Math.Abs(t.At.Y - press.Y) <= t.Radius)
            .OrderBy(t => Math.Abs(t.At.X - press.X) + Math.Abs(t.At.Y - press.Y)).Select(t => t.Node).FirstOrDefault();
        if (hit is not null)
            PointChanged?.Invoke(hit.Node, kind == Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased);
    }

    /// <summary>Opens on the whole tree, zoomed to fit (a mercenary's is small), instead of at the Basic skills.</summary>
    public bool FitWhole { get; set; }

    /// <summary>Opens at the Basic skills like the game, zoomed to show most of the tree's width.</summary>
    private void OpenAtBasic()
    {
        if (_openAt is not { } p || _scroll.ViewportWidth <= 0)
            return;
        _openAt = null;
        if (FitWhole)
        {
            var fit = (float)Math.Clamp(Math.Min(_scroll.ViewportWidth / _canvas.Width, _scroll.ViewportHeight / _canvas.Height), _scroll.MinZoomFactor, _scroll.MaxZoomFactor);
            _scroll.ChangeView(0, 0, fit, true);
            return;
        }
        var zoom = (float)Math.Clamp(Math.Max(_scroll.ViewportWidth / _canvas.Width, 0.75), _scroll.MinZoomFactor, _scroll.MaxZoomFactor);
        _scroll.ChangeView(Math.Max(0, p.X * zoom - _scroll.ViewportWidth / 2), Math.Max(0, p.Y * zoom - _scroll.ViewportHeight / 3), zoom, true);
    }

    // The leveling step before the shown one: nodes with more points now than then are drawn green, not gold.
    private IReadOnlyDictionary<int, int>? _previous;

    /// <param name="ranks">Points per node in the build (node id -> rank).</param>
    /// <param name="previous">The points at the leveling step before, to mark what this step adds; null for none.</param>
    public void Show(SkillTree tree, IReadOnlyDictionary<int, int> ranks, IReadOnlyDictionary<int, int>? previous = null)
    {
        _canvas.Children.Clear();
        _targets.Clear();
        _previous = previous;
        var sameTree = ReferenceEquals(tree, _shown); // a point changed: keep the view where it is
        _shown = tree;
        if (tree.Nodes.Count == 0)
            return;

        // The game draws the tree downward from the Basic skills, which sit at the lowest y; screen y also grows downward.
        double minX = tree.Nodes.Min(n => n.X), minY = tree.Nodes.Min(n => n.Y);
        // The game draws y 1.875x taller than x (measured on captures); match it so the tree looks the same.
        const double yScale = PixelsPerUnit * Deckard.Perception.SkillTrees.SkillTreeLocator.VerticalStretch;
        Point At(double x, double y) => new((x - minX) * PixelsPerUnit + Border, (y - minY) * yScale + Border);
        _canvas.Width = (tree.Nodes.Max(n => n.X) - minX) * PixelsPerUnit + 2 * Border;
        _canvas.Height = (tree.Nodes.Max(n => n.Y) - minY) * yScale + 2 * Border;

        var nodes = tree.Nodes.ToDictionary(n => n.Node);
        bool Lit(SkillNode n) => ranks.GetValueOrDefault(n.Node) > 0;

        // Trunk: the game joins the cluster hubs in unlock order with a thick red line.
        var trunk = tree.Nodes.Where(n => n.Kind == "cluster").OrderBy(n => n.RequiredLevel).Select(n => At(n.X, n.Y)).ToList();
        foreach (var (brush, thickness) in new[] { (TrunkDark, 12.0), (TrunkLight, 5.0) })
        {
            var line = new Polyline { Stroke = brush, StrokeThickness = thickness };
            trunk.ForEach(line.Points.Add);
            _canvas.Children.Add(line);
        }

        // Branches: lit when they lead to a node of the build (junctions just pass the path through).
        var lineDirections = new Dictionary<int, List<Point>>(); // per node: unit vectors of the lines leaving it
        foreach (var link in tree.Links)
        {
            if (!nodes.TryGetValue(link.A, out var a) || !nodes.TryGetValue(link.B, out var b))
                continue;
            var onPath = (Lit(a) || Lit(b)) && (Lit(a) || a.Kind is "junction" or "cluster") && (Lit(b) || b.Kind is "junction" or "cluster");
            var line = new Polyline { Stroke = onPath ? GoldLine : Line, StrokeThickness = onPath ? 3 : 1.5 };
            line.Points.Add(At(a.X, a.Y));
            foreach (var corner in link.Corners)
                line.Points.Add(At(corner[0], corner[1]));
            line.Points.Add(At(b.X, b.Y));
            _canvas.Children.Add(line);
            AddDirection(lineDirections, a.Node, line.Points[0], line.Points[1]);
            AddDirection(lineDirections, b.Node, line.Points[^1], line.Points[^2]);
        }

        foreach (var node in tree.Nodes)
        {
            _canvas.Children.Add(NodeElement(node, ranks.GetValueOrDefault(node.Node), At(node.X, node.Y)));
            if (node.Kind is "skill" or "modifier" or "passive")
                _targets.Add((node, At(node.X, node.Y), NodeBounds(node, At(node.X, node.Y)).Width / 2 + 2));
        }

        // Points on the small nodes go last, on top of lines and nodes, on the side that its own lines and the nodes
        // close by leave free.
        var positions = tree.Nodes.Select(n => (Node: n, At: At(n.X, n.Y))).ToList();
        // Space already taken: every node's frame and each skill's rank label; badges add theirs as they're placed.
        var occupied = positions.Select(n => NodeBounds(n.Node, n.At)).ToList();
        occupied.AddRange(positions.Where(n => n.Node.Kind == "skill").Select(n => new Rect(n.At.X - SkillSize / 2, n.At.Y + SkillSize / 2, SkillSize, 16)));
        foreach (var (node, at) in positions.Where(n => n.Node.Kind is "modifier" or "passive"))
        {
            var blocked = new List<Point>(lineDirections.GetValueOrDefault(node.Node) ?? []);
            foreach (var (other, otherAt) in positions)
                if (other != node && Math.Abs(otherAt.X - at.X) < 45 && Math.Abs(otherAt.Y - at.Y) < 45)
                    AddDirection(blocked, at, otherAt);
            if (PointsBadge(node, ranks.GetValueOrDefault(node.Node), at, blocked, occupied) is not { } badge)
                continue;
            occupied.Add(new Rect(Canvas.GetLeft(badge), Canvas.GetTop(badge), badge.DesiredSize.Width, badge.DesiredSize.Height));
            _canvas.Children.Add(badge);
        }

        if (sameTree)
            return;
        _openAt = trunk.Count > 0 ? trunk[0] : new Point(_canvas.Width / 2, _canvas.Height);
        OpenAtBasic();
    }

    /// <summary>A taken node's colour: green when the shown leveling step adds points to it, else gold.</summary>
    private SolidColorBrush Taken(SkillNode node, int rank) => rank > (_previous?.GetValueOrDefault(node.Node) ?? rank) ? Fresh : Gold;

    private FrameworkElement NodeElement(SkillNode node, int rank, Point p)
    {
        FrameworkElement element = node.Kind switch
        {
            "cluster" => Diamond(ClusterSize, ClusterFill, TrunkLight, 3, Icon(node.Icon, ClusterSize * 0.85)), // category emblem
            "junction" => new Ellipse { Width = JunctionSize, Height = JunctionSize, Fill = Line },
            "modifier" => Diamond(ModifierSize, NodeFill, rank > 0 ? Taken(node, rank) : Bronze, rank > 0 ? 2 : 1, Icon(node.Icon, ModifierSize * 0.62)),
            "passive" => Framed(new Ellipse { Width = PassiveSize, Height = PassiveSize, Fill = NodeFill, Stroke = rank > 0 ? Taken(node, rank) : Bronze, StrokeThickness = rank > 0 ? 2 : 1 }, Icon(node.Icon, PassiveSize - 8)),
            _ => Skill(node, rank),
        };
        if (node.Kind is "skill" or "modifier" or "passive")
        {
            element.Opacity = rank > 0 ? 1 : 0.42;
            ToolTipService.SetToolTip(element, Tooltip(node, rank));
        }
        else if (node.Kind == "cluster" && node.Name.Length > 0)
        {
            ToolTipService.SetToolTip(element, $"{node.Name} skills" + (node.RequiredLevel > 0 ? $"\nunlocks at level {node.RequiredLevel}" : ""));
        }
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Centre the node's square/diamond/dot on its position (a skill's rank label hangs below it).
        var size = node.Kind switch { "skill" => SkillSize, "modifier" => ModifierSize * 1.42, "passive" => PassiveSize, "cluster" => ClusterSize * 1.42, _ => JunctionSize };
        Canvas.SetLeft(element, p.X - size / 2);
        Canvas.SetTop(element, p.Y - size / 2);
        return element;
    }

    /// <summary>The node's tooltip, laid out like the game's: name, points and unlock level, then the description.</summary>
    private StackPanel Tooltip(SkillNode node, int rank)
    {
        var panel = new StackPanel { MaxWidth = 380, Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = node.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Gold, TextWrapping = TextWrapping.Wrap });
        var points = rank > 0 ? $"Build: {rank}/{node.MaxRanks} points" : $"Not in the build · {node.MaxRanks} point{(node.MaxRanks == 1 ? "" : "s")} max";
        if (_previous is not null && rank > _previous.GetValueOrDefault(node.Node))
            points += $" (+{rank - _previous.GetValueOrDefault(node.Node)} this step)";
        panel.Children.Add(new TextBlock { Text = points + (node.RequiredLevel > 0 ? $" · unlocks at level {node.RequiredLevel}" : ""), FontSize = 12, Opacity = 0.75 });
        if (node.DescAt(rank) is { } desc && !string.IsNullOrWhiteSpace(desc))
            panel.Children.Add(new TextBlock { Text = desc, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        if (Editing)
            panel.Children.Add(new TextBlock { Text = "Click to add a point, right-click to remove one.", FontSize = 12, Opacity = 0.75, Margin = new Thickness(0, 4, 0, 0) });
        return panel;
    }

    private static void AddDirection(Dictionary<int, List<Point>> directions, int node, Point from, Point toward)
    {
        if (!directions.TryGetValue(node, out var list))
            directions[node] = list = [];
        AddDirection(list, from, toward);
    }

    private static void AddDirection(List<Point> list, Point from, Point toward)
    {
        double dx = toward.X - from.X, dy = toward.Y - from.Y, length = Math.Sqrt(dx * dx + dy * dy);
        if (length >= 0.01)
            list.Add(new Point(dx / length, dy / length));
    }

    /// <summary>
    /// The points of a small node ("1/5") as a badge with a dark background, placed below, above, right or left of the
    /// node, whichever is furthest from the lines leaving it, so the text doesn't sit on the tree.
    /// </summary>
    private static Rect NodeBounds(SkillNode node, Point p)
    {
        var size = node.Kind switch { "skill" => SkillSize, "modifier" => ModifierSize * 1.42, "passive" => PassiveSize, "cluster" => ClusterSize * 1.42, _ => JunctionSize };
        return new Rect(p.X - size / 2, p.Y - size / 2, size, size);
    }

    private static bool Overlaps(Rect a, Rect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private FrameworkElement? PointsBadge(SkillNode node, int rank, Point p, List<Point> lines, List<Rect> occupied)
    {
        var badge = new Microsoft.UI.Xaml.Controls.Border
        {
            Background = Brush(16, 14, 12),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(2, 0, 2, 0),
            IsHitTestVisible = false,
            Opacity = rank > 0 ? 1 : 0.7,
            Child = new TextBlock
            {
                Text = $"{rank}/{node.MaxRanks}",
                FontSize = 9,
                FontWeight = rank > 0 ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = rank > 0 ? Taken(node, rank) : Muted,
            },
        };
        badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = badge.DesiredSize;

        // Sides ranked by how far they are from the node's lines and neighbours (lowest max dot product first); take
        // the best one that doesn't cover a node or another label.
        // Crowded spots fall back to the diagonals, then to all eight positions a step further out.
        const double d = 0.7071;
        Point[] sides = [new(0, 1), new(0, -1), new(1, 0), new(-1, 0)];
        Point[] diagonals = [new(d, d), new(-d, d), new(d, -d), new(-d, -d)];
        var reach = (node.Kind == "passive" ? PassiveSize : ModifierSize * 1.42) / 2 + 1;
        Rect At(Point s, double gap) => new(
            p.X + s.X * (reach + gap + size.Width / 2) - size.Width / 2,
            p.Y + s.Y * (reach + gap + size.Height / 2) - size.Height / 2,
            size.Width, size.Height);
        IEnumerable<Point> Ranked(IEnumerable<Point> s) => s.OrderBy(v => lines.Count == 0 ? 0 : lines.Max(l => l.X * v.X + l.Y * v.Y));
        var candidates = Ranked(sides).Select(s => At(s, 0))
            .Concat(Ranked(diagonals).Select(s => At(s, 0)))
            .Concat(Ranked(sides.Concat(diagonals)).Select(s => At(s, 8)))
            .ToList();
        var free = candidates.Where(r => !occupied.Any(o => Overlaps(r, o))).Select(r => (Rect?)r).FirstOrDefault();
        // No room: a build node still shows its points; an unused node's "0/x" is left to its tooltip.
        if (free is null && rank == 0)
            return null;
        var place = free ?? candidates[0];
        Canvas.SetLeft(badge, place.X);
        Canvas.SetTop(badge, place.Y);
        return badge;
    }

    /// <summary>Square icon with a bronze frame (gold when taken) and its rank underneath, like the game's "0/15".</summary>
    private FrameworkElement Skill(SkillNode node, int rank)
    {
        var frame = new Microsoft.UI.Xaml.Controls.Border
        {
            Width = SkillSize, Height = SkillSize,
            Background = NodeFill,
            BorderBrush = rank > 0 ? Taken(node, rank) : Bronze,
            BorderThickness = new Thickness(rank > 0 ? 3 : 1.5),
            Child = Icon(node.Icon, SkillSize - 6),
        };
        var label = new Microsoft.UI.Xaml.Controls.Border
        {
            Background = Brush(16, 14, 12), // keeps branch lines from running through the text
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(3, 0, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = $"{rank}/{node.MaxRanks}",
                FontSize = 11,
                FontWeight = rank > 0 ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = rank > 0 ? Taken(node, rank) : Muted,
            },
        };
        return new StackPanel { Width = SkillSize, Children = { frame, label } };
    }

    /// <summary>A diamond (rotated square) frame with an optional upright icon in the middle.</summary>
    private static FrameworkElement Diamond(double size, Microsoft.UI.Xaml.Media.Brush fill, Microsoft.UI.Xaml.Media.Brush stroke, double thickness, FrameworkElement? icon)
    {
        var outer = size * 1.42; // the rotated square's bounding box
        var shape = new Rectangle
        {
            Width = size, Height = size, Fill = fill, Stroke = stroke, StrokeThickness = thickness,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform { Angle = 45 },
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var grid = new Grid { Width = outer, Height = outer, Children = { shape } };
        if (icon is not null)
            grid.Children.Add(icon);
        return grid;
    }

    private static FrameworkElement Framed(Shape frame, FrameworkElement icon) =>
        new Grid { Width = frame.Width, Height = frame.Height, Children = { frame, icon } };

    private static Image Icon(long id, double size)
    {
        var image = new Image { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _ = LoadAsync(image, id);
        return image;
    }

    private static async Task LoadAsync(Image image, long id) => image.Source = await IconCache.GetAsync(id);

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));
}
