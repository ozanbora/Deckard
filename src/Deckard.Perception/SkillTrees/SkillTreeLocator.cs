using Deckard.Core.Capture;
using Deckard.Core.Items;

namespace Deckard.Perception.SkillTrees;

/// <summary>
/// Where the in-game skill tree is drawn: screen = (x * ScaleX + OffsetX, y * ScaleY + OffsetY) for tree coordinates.
/// <paramref name="Zoom"/> is the game's tree zoom relative to its default (1); <paramref name="Layout"/> which of the
/// game's skill tree screens it's on.
/// </summary>
public readonly record struct TreePlacement(double OffsetX, double OffsetY, double ScaleX, double ScaleY, double Zoom, ScreenLayout Layout = ScreenLayout.Full)
{
    public (double X, double Y) ToScreen(double x, double y) => (x * ScaleX + OffsetX, y * ScaleY + OffsetY);
}

/// <summary>
/// Finds the open in-game skill tree, how far it is panned and how far it is zoomed, fast enough to run on every
/// captured frame. At default zoom the game draws the tree at 0.0998 px per tree unit across and 0.187 down at 1440p
/// (measured on Season 15 captures, scaling with screen height); zoom scales both. The placement is found by lining
/// up the class's known trunk (red hubs joined by thick red lines) with the red pixels on screen.
/// </summary>
public sealed class SkillTreeLocator
{
    public const double ScaleXPerHeight = 0.0998 / 1440;
    public const double ScaleYPerHeight = 0.187 / 1440;

    /// <summary>How much taller the game draws the tree than its data coordinates (y scale / x scale).</summary>
    public const double VerticalStretch = ScaleYPerHeight / ScaleXPerHeight;

    private const int Step = 4; // analyse every 4th pixel each way
    // Measured on screenshots: the game zooms from ~0.95 to ~2.25 relative to the captures the scale was taken from.
    private const double MinZoom = 0.8, MaxZoom = 2.6;

    /// <summary>
    /// Radius of a hub's red star at zoom 1, per 1440 px of screen height: the red-weighted mean over the band of
    /// rings around its centre that are at least 15% red (measured 42.9-44.7 on two hubs).
    /// </summary>
    private const double HubBandRadiusAt1440 = 43.8;

    private readonly (double X, double Y)[] _hubs;
    private readonly (double X, double Y)[] _trunk; // points along the trunk between hubs (away from the hubs' grey centres)
    private readonly (double X, double Y)[] _nodes; // skills, modifiers, passives and junctions: lighter than the background
    private TreePlacement? _last;

    public SkillTreeLocator(SkillTree tree)
    {
        _hubs = [.. tree.Nodes.Where(n => n.Kind == "cluster").OrderBy(n => n.RequiredLevel).Select(n => ((double)n.X, (double)n.Y))];
        _nodes = [.. tree.Nodes.Where(n => n.Kind != "cluster").Select(n => ((double)n.X, (double)n.Y))];
        var trunk = new List<(double, double)>();
        for (var i = 1; i < _hubs.Length; i++)
            for (var t = 3; t <= 17; t++)
                trunk.Add((_hubs[i - 1].X + (_hubs[i].X - _hubs[i - 1].X) * t / 20.0, _hubs[i - 1].Y + (_hubs[i].Y - _hubs[i - 1].Y) * t / 20.0));
        _trunk = [.. trunk];
    }

    /// <summary>A candidate placement: zoom and the screen offset of tree origin.</summary>
    private readonly record struct Pose(double Zoom, double X, double Y);

    /// <summary>The tree's placement, or null when the skill tree isn't open.</summary>
    public TreePlacement? Locate(Frame frame)
    {
        if (_hubs.Length < 2 || Layout(frame) is not { } layout)
            return _last = null;
        if (_last is { } before && before.Layout != layout)
            _last = null; // the other screen: its tree isn't where this one's was

        int w = frame.Width / Step, h = frame.Height / Step;
        var red = RedMask(frame, w, h);
        var panel = Panel(frame, layout);
        if (layout == ScreenLayout.Docked)
        {
            // Docked, the game world fills the rest of the screen: its red isn't the tree's.
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    if (x * Step < panel.X || x * Step >= panel.Right || y * Step < panel.Y || y * Step >= panel.Bottom)
                        red[y * w + x] = false;
            // Nor is the Skill Assignment chevron's red glow.
            var bar = AssignmentBar(frame.Width, frame.Height, layout);
            for (var y = bar.Y / Step; y < Math.Min(h, bar.Bottom / Step); y++)
                for (var x = bar.X / Step; x < Math.Min(w, bar.Right / Step); x++)
                    red[y * w + x] = false;
        }
        double baseX = frame.Height * ScaleXPerHeight, baseY = frame.Height * ScaleYPerHeight;
        var reach = frame.Height * 0.045;

        double Coarse(Pose p) => Score(red, w, h, panel, p, baseX, baseY, frame.Height);

        // Searches offsets around each start, at a few zooms around its zoom (zooming is anchored on the panel centre,
        // so a zoom change doesn't throw the visible part off screen).
        (double Score, Pose Pose) SearchAround(IEnumerable<Pose> starts, double zoomSpan)
        {
            var best = (Score: 0.0, Pose: default(Pose));
            foreach (var start in starts)
                for (var dz = -zoomSpan; dz <= zoomSpan + 1e-9; dz += 0.025)
                {
                    var zoomed = Rezoom(start, start.Zoom * (1 + dz), panel);
                    if (zoomed.Zoom is < MinZoom or > MaxZoom)
                        continue;
                    for (var dy = -reach; dy <= reach; dy += 8)
                        for (var dx = -reach; dx <= reach; dx += 8)
                        {
                            var pose = zoomed with { X = zoomed.X + dx, Y = zoomed.Y + dy };
                            var score = Coarse(pose);
                            if (score > best.Score)
                                best = (score, pose);
                        }
                }
            return best;
        }

        // Fast path: while dragging or zooming, the tree changes only a little between frames.
        var best = _last is { } last ? SearchAround([new Pose(last.Zoom, last.OffsetX, last.OffsetY)], 0.05) : default;
        if (best.Score < 0.75)
        {
            var hypotheses = Hypotheses(frame, DenseSpots(red, w, h, frame.Height), Stars(red, w, h, frame.Height), baseX, baseY);
            var full = SearchAround(hypotheses.OrderByDescending(Coarse).Take(5), 0.1);
            if (full.Score > best.Score)
                best = full;
        }
        // A trunk fit must also put the nodes on icons: red alone can be fooled, e.g. a trunk shrunk at a wrong zoom
        // until it crowds onto the one visible hub.
        Pose refined = default;
        var trunkFit = best.Score >= 0.6;
        if (trunkFit)
        {
            refined = Refine(frame, panel, best.Pose, baseX, baseY);
            var nodes = NodeScore(frame, panel, refined, baseX, baseY, (double.MinValue, double.MinValue));
            trunkFit = double.IsNaN(nodes) || nodes >= 0.22; // measured: right fits 0.29-0.48, wrong ones 0.11-0.14
        }
        if (!trunkFit)
        {
            // Solves from hub centres and the nodes around them.
            Pose? FromHubs(IEnumerable<(double X, double Y, double Zoom)> hubs) => hubs
                .Select(hub => (Hub: hub, Pose: SolveLoneHub(frame, panel, hub.X, hub.Y, hub.Zoom, baseX, baseY)))
                .Select(c => (c.Pose, Nodes: NodeScore(frame, panel, c.Pose, baseX, baseY, (c.Hub.X, c.Hub.Y))))
                .Where(c => c.Nodes >= 0.25)
                .OrderByDescending(c => c.Nodes)
                .Select(c => (Pose?)c.Pose)
                .FirstOrDefault();

            // The trunk isn't in view (e.g. zoomed in on the last cluster): a red hub star. Locked parts of the tree
            // aren't red at all: follow a drag into them from the last placement by the nodes alone, or else start
            // from a locked hub's gold diamond (opening the tree where it was left), settling the offset by the nodes.
            var lone = FromHubs(Stars(red, w, h, frame.Height).Take(3).Select(s => HubAt(frame, s.X, s.Y)).OfType<(double X, double Y, double Zoom)>())
                ?? (_last is { } seen ? Track(frame, panel, new Pose(seen.Zoom, seen.OffsetX, seen.OffsetY), baseX, baseY) : null);
            if (lone is null && FromHubs(GoldHubs(frame, panel).Take(4).Select(g => ((double)g.X, (double)g.Y, 1.0))) is { } locked)
                lone = Track(frame, panel, locked, baseX, baseY);
            if (lone is null && layout == ScreenLayout.Docked)
                lone = ScanDocked(frame, panel, baseX, baseY);
            if (lone is not { } pose)
                return null; // nothing to go by, or covered by a tooltip: hide rather than show a stale placement (_last still seeds the next search)
            refined = pose;
        }
        if (_last is { } previous)
        {
            // Zoomed in, nearby zooms fit almost equally well and successive frames flip between them: keep the shown
            // zoom unless the new one clearly fits better (a real zoom leaves the old one fitting far worse).
            double Fit(Pose p) => (NodeScore(frame, panel, p, baseX, baseY, (double.MinValue, double.MinValue)) is var n && double.IsNaN(n) ? 0 : n)
                + 0.5 * ExactScore(frame, panel, p, baseX, baseY);
            // Fits are pinned on hubs, so hold the zoom about the hub nearest the panel's centre.
            double cx = panel.X + panel.Width / 2.0, cy = panel.Y + panel.Height / 2.0;
            var (hx, hy) = _hubs.Select(hub => (X: hub.X * baseX * refined.Zoom + refined.X, Y: hub.Y * baseY * refined.Zoom + refined.Y))
                .MinBy(hub => (hub.X - cx) * (hub.X - cx) + (hub.Y - cy) * (hub.Y - cy));
            var f = previous.Zoom / refined.Zoom;
            var keptZoom = new Pose(previous.Zoom, hx - (hx - refined.X) * f, hy - (hy - refined.Y) * f);
            if (Fit(keptZoom) >= Fit(refined) - 0.03)
                refined = keptZoom;
            // A still tree's fit also wobbles by a few pixels (the analysis grid, the animated background): ignore
            // changes that small, measured where they show (far from the tree's origin a tiny zoom change moves the
            // offset a lot).
            if (Moved(previous, refined, panel, baseX, baseY) <= Math.Max(6, frame.Height * 0.004 * refined.Zoom))
                return previous;
        }
        return _last = new TreePlacement(refined.X, refined.Y, baseX * refined.Zoom, baseY * refined.Zoom, refined.Zoom, layout);
    }

    /// <summary>
    /// Candidate poses from pairs of dense red spots matched to pairs of known hubs: the pair's distance gives the zoom,
    /// its direction must agree. Zoomed in, a single hub may be all that's visible, so each spot is also tried as each
    /// hub across the zoom range.
    /// </summary>
    private IEnumerable<Pose> Hypotheses(Frame frame, List<(int X, int Y)> spots, List<(int X, int Y)> stars, double baseX, double baseY)
    {
        // Best first: hub stars found by shape, with the zoom read off each star's size.
        foreach (var (sx, sy) in stars)
        {
            if (HubAt(frame, sx, sy) is not var (cx, cy, zoom))
                continue;
            foreach (var (hx, hy) in _hubs)
                yield return new Pose(zoom, cx - hx * baseX * zoom, cy - hy * baseY * zoom);
        }

        foreach (var (px, py) in spots)
            foreach (var (hx, hy) in _hubs)
                for (var zoom = MinZoom; zoom <= MaxZoom; zoom *= 1.08)
                    yield return new Pose(zoom, px - hx * baseX * zoom, py - hy * baseY * zoom);

        var top = spots.Take(8).ToList();
        for (var i = 0; i < top.Count; i++)
            for (var j = 0; j < top.Count; j++)
            {
                if (i == j)
                    continue;
                double sdx = top[j].X - top[i].X, sdy = top[j].Y - top[i].Y, spotLength = Math.Sqrt(sdx * sdx + sdy * sdy);
                if (spotLength < 40)
                    continue;
                for (var a = 0; a < _hubs.Length; a++)
                    for (var b = 0; b < _hubs.Length; b++)
                    {
                        if (a == b)
                            continue;
                        double hdx = (_hubs[b].X - _hubs[a].X) * baseX, hdy = (_hubs[b].Y - _hubs[a].Y) * baseY, hubLength = Math.Sqrt(hdx * hdx + hdy * hdy);
                        var k = spotLength / hubLength;
                        // Same direction (within ~20°) and a plausible zoom.
                        if (k is < MinZoom or > MaxZoom || (sdx * hdx + sdy * hdy) / (spotLength * hubLength) < 0.94)
                            continue;
                        yield return new Pose(k, top[i].X - _hubs[a].X * baseX * k, top[i].Y - _hubs[a].Y * baseY * k);
                    }
            }
    }

    /// <summary>
    /// Centres of hub stars, found by shape on the analysis grid. From a non-red point, 8 rays go outward to the first
    /// red: a star's centre (its grey emblem) sees red in all 8 directions at similar distances, none close in. At low
    /// zoom the star is a solid red blob; zoomed in it is a thin outline with a wide clear inside, and rays see both. A
    /// point beside a trunk gets one very short ray; one in open background gets a ray that never hits, which rejects
    /// most points after the first ray. Screen pixels, most symmetric first.
    /// </summary>
    internal static List<(int X, int Y)> Stars(bool[] red, int w, int h, int frameHeight)
    {
        var maxRay = (int)(0.12 * frameHeight / Step);       // past the spikes at maximum zoom
        var clear = Math.Max(2.0, 0.012 * frameHeight / Step); // the emblem keeps at least this much clear around the centre (min zoom: ~0.017h)
        (int X, int Y)[] directions = [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)];
        var found = new List<(double Spread, int X, int Y)>();
        for (var y = 0; y < h; y += 3)
            for (var x = 0; x < w; x += 3)
            {
                if (red[y * w + x])
                    continue;
                double nearest = double.MaxValue, farthest = 0;
                var star = true;
                foreach (var (dx, dy) in directions)
                {
                    var length = dx != 0 && dy != 0 ? 1.414 : 1;
                    var hit = 0;
                    for (var d = 1; d <= maxRay; d++)
                    {
                        int nx = x + dx * d, ny = y + dy * d;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                            break;
                        if (red[ny * w + nx])
                        {
                            hit = d;
                            break;
                        }
                    }
                    if (hit == 0 || hit * length < clear)
                    {
                        star = false;
                        break;
                    }
                    nearest = Math.Min(nearest, hit * length);
                    farthest = Math.Max(farthest, hit * length);
                }
                if (star && farthest / nearest <= 2.6)
                    found.Add((farthest / nearest, x, y));
            }
        // One centre per star: most symmetric first, then suppress neighbours.
        var minSeparation = clear * 3;
        var picked = new List<(int X, int Y)>();
        foreach (var (_, x, y) in found.OrderBy(f => f.Spread))
            if (picked.All(p => Math.Abs(p.X - x) > minSeparation || Math.Abs(p.Y - y) > minSeparation))
                picked.Add((x, y));
        return [.. picked.Take(6).Select(p => (p.X * Step, p.Y * Step))];
    }

    /// <summary>
    /// Centres of locked hubs: a locked hub has no red star but a solid olive-gold diamond (~(90,70,10)) about 0.035 of
    /// the screen height across at zoom 1, far bigger than any gold on a node. Screen pixels, densest first.
    /// </summary>
    internal static List<(int X, int Y)> GoldHubs(Frame frame, PixelRect panel)
    {
        int w = frame.Width / Step, h = frame.Height / Step;
        var gold = new bool[w * h];
        var px = frame.Pixels;
        for (var y = panel.Y / Step; y < panel.Bottom / Step; y++)
            for (var x = 0; x < w; x++)
            {
                var o = y * Step * frame.Stride + x * Step * Frame.BytesPerPixel;
                int b = px[o], g = px[o + 1], r = px[o + 2];
                gold[y * w + x] = r > 40 && g > r * 0.6 && g < r * 0.92 && b < r * 0.35;
            }
        return DenseSpots(gold, w, h, frame.Height, win: Math.Max(3, (int)(frame.Height * 0.022 / Step)), minDensity: 0.45f);
    }

    /// <summary>
    /// A hub near a dense red spot: alternately takes the red centroid (window sized from the zoom) and the zoom from
    /// the radial profile at that centre. Null when there's no star there.
    /// </summary>
    internal static (double X, double Y, double Zoom)? HubAt(Frame frame, double x, double y)
    {
        var unit = frame.Height / 1440.0;
        if (HubZoom(frame, x, y) is not { } zoom)
            return null;
        for (var pass = 0; pass < 3; pass++)
        {
            var window = HubBandRadiusAt1440 * unit * zoom * 1.6;
            double sumX = 0, sumY = 0;
            var count = 0;
            for (var py = (int)(y - window); py <= y + window; py += 2)
                for (var px = (int)(x - window); px <= x + window; px += 2)
                {
                    if (px < 0 || py < 0 || px >= frame.Width || py >= frame.Height || (px - x) * (px - x) + (py - y) * (py - y) > window * window)
                        continue;
                    if (IsRedAt(frame, px, py))
                    {
                        sumX += px;
                        sumY += py;
                        count++;
                    }
                }
            if (count < 30)
                return null;
            (x, y) = (sumX / count, sumY / count);
            zoom = HubZoom(frame, x, y) ?? zoom;
        }
        return (x, y, zoom);
    }

    /// <summary>
    /// The zoom implied by a hub's size, from its radial profile: the share of red on rings around its centre. The
    /// star makes a band of mostly-red rings; trunk and allocated (red) branch lines cover only ~5% of any ring, so
    /// they don't shift it, and no zoom guess is needed.
    /// </summary>
    internal static double? HubZoom(Frame frame, double cx, double cy)
    {
        var unit = frame.Height / 1440.0;
        var step = 5 * unit;
        var rings = new List<(double Radius, double Red)>();
        for (var r = 10 * unit; r <= HubBandRadiusAt1440 * MaxZoom * 1.8 * unit; r += step)
        {
            int n = 0, red = 0;
            for (var a = 0; a < 120; a++)
            {
                int x = (int)(cx + r * Math.Cos(a * Math.PI / 60)), y = (int)(cy + r * Math.Sin(a * Math.PI / 60));
                if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height)
                    continue;
                n++;
                if (IsRedAt(frame, x, y))
                    red++;
            }
            rings.Add((r, n == 0 ? 0 : red / (double)n));
        }
        // A star's centre is its grey emblem; a centre inside red (a trunk) isn't a hub.
        if (rings.Count == 0 || rings[0].Red > 0.5)
            return null;
        var peak = rings.FindIndex(r => r.Red == rings.Max(m => m.Red));
        if (peak < 0 || rings[peak].Red < 0.25)
            return null;
        int first = peak, last = peak;
        while (first > 0 && rings[first - 1].Red >= 0.15)
            first--;
        while (last < rings.Count - 1 && rings[last + 1].Red >= 0.15)
            last++;
        var band = rings.Skip(first).Take(last - first + 1).ToList();
        var radius = band.Sum(r => r.Radius * r.Red) / band.Sum(r => r.Red);
        return Math.Clamp(radius / (HubBandRadiusAt1440 * unit), MinZoom, MaxZoom);
    }

    /// <summary>
    /// Zooming about a lone hub leaves the trunk through it in place, and hub designs differ too much for their size to
    /// give the zoom. The nodes around the hub do: dozens of icons at known places, lighter than the background, that a
    /// wrong zoom (or a wrong guess of which hub this is) misses. Tries every hub across the whole zoom range, then
    /// refines the zoom. Falls back to the hub's size when no nodes are in view.
    /// </summary>
    private Pose SolveLoneHub(Frame frame, PixelRect panel, double mx, double my, double sizeZoom, double baseX, double baseY)
    {
        Pose At((double X, double Y) hub, double zoom) => new(zoom, mx - hub.X * baseX * zoom, my - hub.Y * baseY * zoom);

        // Without nodes in view to judge by, the hub's size is the best zoom there is.
        if (NodeScore(frame, panel, At(_hubs[0], sizeZoom), baseX, baseY, (mx, my)) is double.NaN
            && NodeScore(frame, panel, At(_hubs[^1], sizeZoom), baseX, baseY, (mx, my)) is double.NaN)
            return _hubs.Select(hub => At(hub, sizeZoom)).MaxBy(p => ExactScore(frame, panel, p, baseX, baseY));

        double Fit(Pose p)
        {
            var nodes = NodeScore(frame, panel, p, baseX, baseY, (mx, my));
            return (double.IsNaN(nodes) ? 0 : nodes) + 0.5 * ExactScore(frame, panel, p, baseX, baseY);
        }
        var zooms = new List<double>();
        for (var z = MinZoom; z <= MaxZoom; z *= 1.015)
            zooms.Add(z);
        var (bestHub, bestZoom, bestFit) = (_hubs[0], sizeZoom, double.MinValue);
        foreach (var hub in _hubs)
            foreach (var z in zooms)
            {
                var fit = Fit(At(hub, z));
                if (fit > bestFit)
                    (bestHub, bestZoom, bestFit) = (hub, z, fit);
            }
        // The trunk helps pick the hub but nudges the zoom; settle the zoom on the nodes alone (within ±8%).
        var fine = Enumerable.Range(-16, 33).Select(i => bestZoom * (1 + i * 0.005)).Where(z => z is >= MinZoom and <= MaxZoom)
            .Select(z => (Zoom: z, Fit: NodeScore(frame, panel, At(bestHub, z), baseX, baseY, (mx, my)))).Where(f => !double.IsNaN(f.Fit)).ToList();
        if (fine.Count == 0)
            return At(bestHub, bestZoom);
        var top = fine.Max(f => f.Fit);
        return At(bestHub, fine.Where(f => f.Fit >= top - 0.01).Average(f => f.Zoom));
    
    }

    /// <summary>
    /// The placement near <paramref name="seed"/> that puts the most nodes on icons, for views with no red to fit:
    /// offsets within 0.12 of the screen height (a fast drag between two analysed frames), then the zoom. Null when
    /// nothing scores like a real fit.
    /// </summary>
    private Pose? Track(Frame frame, PixelRect panel, Pose seed, double baseX, double baseY)
    {
        double Nodes(Pose p) => NodeScore(frame, panel, p, baseX, baseY, (double.MinValue, double.MinValue)) is var s && double.IsNaN(s) ? 0 : s;
        // Icons are wider than the step, so the best score is a plateau: take its centre.
        (double Score, Pose Pose) Best(IEnumerable<Pose> poses)
        {
            var scored = poses.Select(p => (Score: Nodes(p), Pose: p)).ToList();
            var top = scored.Max(s => s.Score);
            var plateau = scored.Where(s => s.Score >= top - 0.005).Select(s => s.Pose).ToList();
            return (top, new Pose(plateau.Average(p => p.Zoom), plateau.Average(p => p.X), plateau.Average(p => p.Y)));
        }

        double reach = frame.Height * 0.12, step = Math.Max(4, frame.Height * 0.015 * seed.Zoom); // under an icon's width
        var coarse = Best(from dy in Steps(reach, step) from dx in Steps(reach, step) select seed with { X = seed.X + dx, Y = seed.Y + dy });
        var fine = Best(from dy in Steps(step, 3) from dx in Steps(step, 3) select coarse.Pose with { X = coarse.Pose.X + dx, Y = coarse.Pose.Y + dy });
        var zoomed = Best(Enumerable.Range(-3, 7).Select(i => Rezoom(fine.Pose, Math.Clamp(fine.Pose.Zoom * (1 + i * 0.01), MinZoom, MaxZoom), panel)));
        return zoomed.Score >= 0.2 ? zoomed.Pose : null; // measured on a locked Warlock view: right 0.25-0.30, off by a cluster ~0.05

        static IEnumerable<double> Steps(double reach, double step)
        {
            for (var d = -reach; d <= reach + 1e-9; d += step)
                yield return d;
        }
    }


    /// <summary>
    /// Docked, the panel only scrolls up and down, at one zoom (measured at 3440x1440: offset x 2985-2989, zoom
    /// 1.13-1.15 in every docked view): the scroll that puts the most nodes on icons, for views where the hubs are out
    /// of sight (the bottom of the tree) or a scroll jumped past what <see cref="Track"/> follows. Null when nothing
    /// scores like a real fit.
    /// </summary>
    private Pose? ScanDocked(Frame frame, PixelRect panel, double baseX, double baseY)
    {
        var seed = _last is { Layout: ScreenLayout.Docked } last ? new Pose(last.Zoom, last.OffsetX, last.OffsetY)
            : new Pose(1.14, frame.Width - frame.Height * 0.3146, 0);
        double Nodes(Pose p) => NodeScore(frame, panel, p, baseX, baseY, (double.MinValue, double.MinValue)) is var s && double.IsNaN(s) ? 0 : s;
        var scaleY = baseY * seed.Zoom;
        double top = _nodes.Min(n => n.Y), bottom = _nodes.Max(n => n.Y);
        // The game keeps the tree over the panel: its top above the panel's lower quarter, its bottom below the upper one
        // (at the bottom of the scroll, measured: the last nodes 0.4 down the panel). Further out, a few nodes in view
        // scored like a fit (a zoomed-in view put the tree's top below the panel).
        double from = panel.Y + panel.Height * 0.25 - bottom * scaleY, to = panel.Bottom - panel.Height * 0.25 - top * scaleY;
        var step = Math.Max(4, frame.Height * 0.015 * seed.Zoom); // under an icon's height
        var best = (Score: 0.0, Pose: seed);
        for (var y = from; y <= to; y += step)
            if (Nodes(seed with { Y = y }) is var s && s > best.Score)
                best = (s, seed with { Y = y });
        for (var dy = -step; dy <= step; dy += 3)
            if (Nodes(best.Pose with { Y = best.Pose.Y + dy }) is var s && s > best.Score)
                best = (s, best.Pose with { Y = best.Pose.Y + dy });
        return best.Score >= 0.4 ? best.Pose : null; // measured: right fits 0.5-0.6, a zoomed-in view's wrong one 0.32
    }

    /// <summary>How far the panel's corners move on screen between two placements.</summary>
    private static double Moved(TreePlacement from, Pose to, PixelRect panel, double baseX, double baseY)
    {
        var moved = 0.0;
        foreach (var (x, y) in new[] { (panel.X, panel.Y), (panel.Right, panel.Y), (panel.X, panel.Bottom), (panel.Right, panel.Bottom) })
        {
            double tx = (x - from.OffsetX) / from.ScaleX, ty = (y - from.OffsetY) / from.ScaleY;
            moved = Math.Max(moved, Math.Max(Math.Abs(tx * baseX * to.Zoom + to.X - x), Math.Abs(ty * baseY * to.Zoom + to.Y - y)));
        }
        return moved;
    }

    /// <summary>The same view at another zoom, keeping the tree point under the panel centre in place.</summary>
    private static Pose Rezoom(Pose pose, double zoom, PixelRect panel)
    {
        double cx = panel.X + panel.Width / 2.0, cy = panel.Y + panel.Height / 2.0, f = zoom / pose.Zoom;
        return new Pose(zoom, cx - (cx - pose.X) * f, cy - (cy - pose.Y) * f);
    }

    /// <summary>
    /// The coarse score tolerates ±8 px, so it is flat near the answer. Refine on full-resolution pixels, where a point
    /// is either on the 12 px trunk or not: offset, then zoom, then offset again, each time taking the centre of the
    /// best-scoring area.
    /// </summary>
    private Pose Refine(Frame frame, PixelRect panel, Pose pose, double baseX, double baseY)
    {
        Pose Best(IEnumerable<Pose> poses)
        {
            var scored = poses.Select(p => (Score: ExactScore(frame, panel, p, baseX, baseY), Pose: p)).ToList();
            var top = scored.Max(s => s.Score);
            var plateau = scored.Where(s => s.Score >= top - 0.005).Select(s => s.Pose).ToList();
            return new Pose(plateau.Average(p => p.Zoom), plateau.Average(p => p.X), plateau.Average(p => p.Y));
        }
        IEnumerable<Pose> Offsets(Pose p) =>
            from dy in Enumerable.Range(-10, 21) from dx in Enumerable.Range(-10, 21) select p with { X = p.X + dx, Y = p.Y + dy };

        pose = Best(Offsets(pose));
        pose = Best(Enumerable.Range(-6, 13).Select(i => Rezoom(pose, pose.Zoom * (1 + i * 0.005), panel)));
        pose = Best(Offsets(pose));
        return FitHubCentres(frame, panel, pose, baseX, baseY);
    }

    /// <summary>
    /// A hub's red star is symmetric, so the centroid of its red pixels is the hub's centre. Measures that for each
    /// visible hub and fits zoom and offset to them by least squares (offset only with a single hub). Scoring alone is
    /// loose along the trunk; this pins the hubs to a few pixels.
    /// </summary>
    private Pose FitHubCentres(Frame frame, PixelRect panel, Pose pose, double baseX, double baseY)
    {
        var radius = frame.Height * 0.045 * pose.Zoom; // a little past the star's spikes
        var pairs = new List<(double Ax, double Ay, double Mx, double My)>(); // hub at zoom 1 (relative), measured centre
        foreach (var (hx, hy) in _hubs)
        {
            double cx = hx * baseX * pose.Zoom + pose.X, cy = hy * baseY * pose.Zoom + pose.Y;
            if (cx - radius < panel.X || cx + radius >= panel.Right || cy - radius < panel.Y || cy + radius >= panel.Bottom)
                continue;
            double sumX = 0, sumY = 0;
            int count = 0, total = 0;
            for (var y = (int)(cy - radius); y <= cy + radius; y += 2)
                for (var x = (int)(cx - radius); x <= cx + radius; x += 2)
                {
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > radius * radius)
                        continue;
                    total++;
                    if (IsRedAt(frame, x, y))
                    {
                        sumX += x;
                        sumY += y;
                        count++;
                    }
                }
            if (count > total * 0.12) // enough red to be a hub, not just the trunk passing through
                pairs.Add((hx * baseX, hy * baseY, sumX / count, sumY / count));
        }
        if (pairs.Count == 0)
            return pose;

        // Each hub's size gives an independent zoom reading; it guards the fit below.
        var sized = pairs.Select(p => (Pair: p, Zoom: HubZoom(frame, p.Mx, p.My))).Where(s => s.Zoom is not null).ToList();
        var sizeZoom = sized.Count > 0 ? sized.Select(s => s.Zoom!.Value).Order().ElementAt(sized.Count / 2) : pose.Zoom;

        Pose LoneHub()
        {
            var (mx, my) = sized.Count > 0 ? (sized[0].Pair.Mx, sized[0].Pair.My) : (pairs[0].Mx, pairs[0].My);
            return SolveLoneHub(frame, panel, mx, my, sizeZoom, baseX, baseY);
        }
        if (pairs.Count == 1)
            return LoneHub();

        // Minimise sum (k*Ax + X - Mx)^2 + (k*Ay + Y - My)^2 over k, X, Y: solve the 3x3 normal equations.
        double n = pairs.Count, sa = 0, sax = 0, say = 0, smx = 0, smy = 0, samx = 0;
        foreach (var (ax, ay, mx, my) in pairs)
        {
            sa += ax * ax + ay * ay;
            sax += ax;
            say += ay;
            smx += mx;
            smy += my;
            samx += ax * mx + ay * my;
        }
        // From d/dX and d/dY: X = (smx - k*sax)/n, Y = (smy - k*say)/n; substitute into d/dk.
        var denominator = sa - (sax * sax + say * say) / n;
        if (Math.Abs(denominator) < 1e-9)
            return pose;
        var k = (samx - (sax * smx + say * smy) / n) / denominator;
        // "Hubs" found at a wrong pose can be other red (allocated nodes); then the fit disagrees with the hubs' size.
        if (k is < MinZoom or > MaxZoom || Math.Abs(k / sizeZoom - 1) > 0.12)
            return LoneHub();
        return new Pose(k, (smx - k * sax) / n, (smy - k * say) / n);
    }

    /// <summary>The tree's panel on screen; the game clips the tree to it.</summary>
    public static PixelRect Panel(Frame frame, ScreenLayout layout = ScreenLayout.Full) => Panel(frame.Width, frame.Height, layout);

    /// <remarks>
    /// Full: zoomed in, the tree fills the screen's width between the top bar (tabs) and the bottom bar. Docked: the
    /// Abilities panel on the right, below its Available Points box (measured at 3440x1440: x 2547-3418, y 302-1332).
    /// </remarks>
    public static PixelRect Panel(int width, int height, ScreenLayout layout = ScreenLayout.Full) => layout == ScreenLayout.Full
        ? new(0, (int)(height * 0.05), width, (int)(height * 0.87))
        : new(width - (int)(height * 0.62), (int)(height * 0.21), (int)(height * 0.605), (int)(height * 0.715));

    /// <summary>
    /// The game's Skill Assignment bar at the bottom centre, with its expand button on top: no marks go over it.
    /// Measured at 3440x1440: x 1390-2060, y 1235-1360. Docked, its glowing chevron at the panel's bottom centre: x
    /// 2900-3070, y 1210-1340.
    /// </summary>
    public static PixelRect AssignmentBar(int width, int height, ScreenLayout layout = ScreenLayout.Full) => layout == ScreenLayout.Full
        ? new(width / 2 - (int)(height * 0.235), (int)(height * 0.857), (int)(height * 0.47), (int)(height * 0.088))
        : new(width - (int)(height * 0.375), (int)(height * 0.84), (int)(height * 0.12), (int)(height * 0.09));

    /// <summary>
    /// Which skill tree screen is open: the "Skill Tree" tab lit red, on the game's dark tab bar (full screen: top, left of
    /// centre) or in the Abilities panel's frame (docked); null when neither. A red-lit world, the Worldstone Chamber, once
    /// passed for the tab alone.
    /// </summary>
    public static ScreenLayout? Layout(Frame frame)
    {
        var h = frame.Height;
        // Full screen: the lit tab spans y 0.025h..0.038h (1440p: 36..54), x ±0.025h around centre - 0.078h.
        if (TabLit(frame, frame.Width / 2 - (int)(h * 0.078), (int)(h * 0.025), (int)(h * 0.038)) && TabBarDark(frame))
            return ScreenLayout.Full;
        // Docked: x 2800-2930, y 124-170 at 3440x1440 (right edge - 0.403h), the Paragon tab's height.
        if (TabLit(frame, frame.Width - (int)(h * 0.403), (int)(h * 0.092), (int)(h * 0.112)) && DockedPanelDark(frame))
            return ScreenLayout.Docked;
        return null;
    }

    public static bool IsOpen(Frame frame) => Layout(frame) is not null;

    /// <summary>
    /// The docked Abilities panel's dark frame: its header and the strip below it (measured RGB ~20,25,25 at 0.3-0.58h
    /// from the right edge), not a red-lit world. The same on every panel on the right (Character, Abilities, Soul Shards,
    /// Mercenaries): the header's spot is 0.34h out, as further out a lit tab icon sits on some of them.
    /// </summary>
    public static bool DockedPanelDark(Frame frame)
    {
        int h = frame.Height, w = frame.Width;
        return Deckard.Perception.Imaging.ImageOps.IsDarkPanel(frame, w - (int)(h * 0.34), (int)(h * 0.025))
            && Deckard.Perception.Imaging.ImageOps.IsDarkPanel(frame, w - (int)(h * 0.58), (int)(h * 0.05))
            && Deckard.Perception.Imaging.ImageOps.IsDarkPanel(frame, w - (int)(h * 0.3), (int)(h * 0.05));
    }

    /// <summary>
    /// A panel open on the right by its title, light text at the header's left ("CHARACTER", "ABILITIES", "MERCENARIES"):
    /// measured at 3440x1440, 13-17% of the band x 0.42-0.62h from the right edge, y 0.006-0.036h is lit on every panel,
    /// none in a near-black cave or on the character select screen, which <see cref="DockedPanelDark"/> alone took for one.
    /// </summary>
    public static bool SidePanelTitled(Frame frame)
    {
        int h = frame.Height, w = frame.Width;
        int x0 = w - (int)(h * 0.62), x1 = w - (int)(h * 0.42), y0 = (int)(h * 0.006), y1 = (int)(h * 0.036);
        int n = 0, lit = 0;
        var px = frame.Pixels;
        for (var y = y0; y < y1; y += 2)
            for (var x = Math.Max(0, x0); x < x1; x += 2)
            {
                var o = y * frame.Stride + x * Frame.BytesPerPixel;
                int b = px[o], g = px[o + 1], r = px[o + 2];
                n++;
                if (r > 140 && g > 70 && r >= b)
                    lit++;
            }
        return n > 0 && lit >= n * 0.08;
    }

    /// <summary>The dark tab bar either side of the tabs (measured at 3440x1440: RGB ~16,19,19 at 0.25h and 0.4h out).</summary>
    public static bool TabBarDark(Frame frame)
    {
        int h = frame.Height, cx = frame.Width / 2, y = (int)(h * 0.03);
        return Deckard.Perception.Imaging.ImageOps.IsDarkPanel(frame, cx - (int)(h * 0.25), y)
            && Deckard.Perception.Imaging.ImageOps.IsDarkPanel(frame, cx + (int)(h * 0.25), y)
            && Deckard.Perception.Imaging.ImageOps.IsDarkPanel(frame, cx - (int)(h * 0.4), y);
    }

    private static bool TabLit(Frame frame, int cx, int y0, int y1)
    {
        var rx = (int)(frame.Height * 0.025);
        int lit = 0, total = 0;
        var px = frame.Pixels;
        for (var y = y0; y <= y1; y++)
            for (var x = Math.Max(0, cx - rx); x <= Math.Min(frame.Width - 1, cx + rx); x += 2)
            {
                var o = y * frame.Stride + x * Frame.BytesPerPixel;
                int b = px[o], g = px[o + 1], r = px[o + 2];
                total++;
                if (r > 70 && r > 3 * g && r > 3 * b)
                    lit++;
            }
        return lit > total * 0.5;
    }

    /// <summary>Red-dominant pixels (trunk and hubs range from (45,0,0) to (150,20,20)), sampled every Step pixels.</summary>
    internal static bool[] RedMask(Frame frame, int w, int h)
    {
        var mask = new bool[w * h];
        var px = frame.Pixels;
        for (var y = 0; y < h; y++)
        {
            var row = y * Step * frame.Stride;
            for (var x = 0; x < w; x++)
            {
                var o = row + x * Step * Frame.BytesPerPixel;
                int b = px[o], g = px[o + 1], r = px[o + 2];
                mask[y * w + x] = IsRed(r, g, b);
            }
        }
        return mask;
    }

    private static bool IsRed(int r, int g, int b) => r > 38 && r > 3 * g + 12 && r > 3 * b + 12;

    /// <summary>Centres of the densest red areas (hubs, mostly), strongest first; screen pixels.</summary>
    internal static List<(int X, int Y)> DenseSpots(bool[] red, int w, int h, int frameHeight, int win = 0, float minDensity = 0.2f)
    {
        if (win == 0)
            win = Math.Max(6, frameHeight / 22 / Step);
        var half = win / 2;
        var integral = new int[(w + 1) * (h + 1)];
        for (var y = 0; y < h; y++)
        {
            var row = 0;
            for (var x = 0; x < w; x++)
            {
                row += red[y * w + x] ? 1 : 0;
                integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + row;
            }
        }
        var spots = new List<(float Density, int X, int Y)>();
        for (var y = half; y < h - half; y += 2)
            for (var x = half; x < w - half; x += 2)
            {
                var sum = integral[(y + half) * (w + 1) + x + half] - integral[(y - half) * (w + 1) + x + half]
                        - integral[(y + half) * (w + 1) + x - half] + integral[(y - half) * (w + 1) + x - half];
                var density = sum / (float)(win * win);
                if (density > minDensity)
                    spots.Add((density, x, y));
            }
        var picked = new List<(int, int)>();
        foreach (var (_, x, y) in spots.OrderByDescending(s => s.Density))
        {
            if (picked.All(p => Math.Abs(p.Item1 - x) > win || Math.Abs(p.Item2 - y) > win))
                picked.Add((x, y));
            if (picked.Count == 12)
                break;
        }
        return [.. picked.Select(p => (p.Item1 * Step, p.Item2 * Step))];
    }

    // A hub is a red spiky frame around a grey emblem: score it on a ring of points over the frame.
    private static readonly (double X, double Y)[] Ring = [.. Enumerable.Range(0, 8).Select(i => (Math.Cos(i * Math.PI / 4), Math.Sin(i * Math.PI / 4)))];

    /// <summary>Share of the trunk and hub-frame sample points inside the panel that land on red; 0 if too little is visible.</summary>
    private double Score(bool[] red, int w, int h, PixelRect panel, Pose pose, double baseX, double baseY, int frameHeight)
    {
        int visible = 0, hits = 0;
        double sx = baseX * pose.Zoom, sy = baseY * pose.Zoom;
        void Sample(double screenX, double screenY)
        {
            if (screenX < panel.X || screenX >= panel.Right || screenY < panel.Y || screenY >= panel.Bottom)
                return;
            visible++;
            if (RedNear(red, w, h, (int)(screenX / Step), (int)(screenY / Step)))
                hits++;
        }
        foreach (var (x, y) in _trunk)
            Sample(x * sx + pose.X, y * sy + pose.Y);
        var ring = frameHeight * 0.028 * pose.Zoom; // hub frame radius, ~40 px at 1440p and default zoom
        foreach (var (x, y) in _hubs)
            foreach (var (rx, ry) in Ring)
                Sample(x * sx + pose.X + rx * ring, y * sy + pose.Y + ry * ring);
        return visible < 12 ? 0 : (double)hits / visible;
    }

    /// <summary>Like <see cref="Score"/> but on exact full-resolution pixels, for sub-grid refinement.</summary>
    private double ExactScore(Frame frame, PixelRect panel, Pose pose, double baseX, double baseY)
    {
        int visible = 0, hits = 0;
        double sx = baseX * pose.Zoom, sy = baseY * pose.Zoom;
        void Sample(double screenX, double screenY)
        {
            if (screenX < panel.X || screenX >= panel.Right || screenY < panel.Y || screenY >= panel.Bottom)
                return;
            visible++;
            if (IsRedAt(frame, (int)screenX, (int)screenY))
                hits++;
        }
        foreach (var (x, y) in _trunk)
            Sample(x * sx + pose.X, y * sy + pose.Y);
        var ring = frame.Height * 0.028 * pose.Zoom;
        foreach (var (x, y) in _hubs)
            foreach (var (rx, ry) in Ring)
                Sample(x * sx + pose.X + rx * ring, y * sy + pose.Y + ry * ring);
        return visible == 0 ? 0 : (double)hits / visible;
    }

    /// <summary>
    /// Share of the tree's nodes (inside the panel) that land on something lighter than the dark background: an icon,
    /// a frame or a junction dot. Each node is checked at its centre and four points around it, as icons have dark
    /// spots. Nodes predicted inside the given hub's own area are skipped: at too small a zoom they would all land on
    /// the hub's light emblem and score falsely high. NaN when fewer than 8 nodes are visible.
    /// </summary>
    private double NodeScore(Frame frame, PixelRect panel, Pose pose, double baseX, double baseY, (double X, double Y) hub)
    {
        var reach = Math.Max(2, (int)(frame.Height * 0.006 * pose.Zoom));
        var hubArea = frame.Height * 0.13; // the star's size at maximum zoom
        double sx = baseX * pose.Zoom, sy = baseY * pose.Zoom;
        int visible = 0, hits = 0;
        foreach (var (nx, ny) in _nodes)
        {
            int x = (int)(nx * sx + pose.X), y = (int)(ny * sy + pose.Y);
            if (x - reach < panel.X || x + reach >= panel.Right || y - reach < panel.Y || y + reach >= panel.Bottom)
                continue;
            if ((x - hub.X) * (x - hub.X) + (y - hub.Y) * (y - hub.Y) < hubArea * hubArea)
                continue;
            visible++;
            if (Light(x, y) || Light(x + reach, y) || Light(x - reach, y) || Light(x, y + reach) || Light(x, y - reach))
                hits++;
        }
        return visible < 8 ? double.NaN : (double)hits / visible;

        bool Light(int x, int y)
        {
            var o = y * frame.Stride + x * Frame.BytesPerPixel;
            var px = frame.Pixels;
            return px[o] + px[o + 1] + px[o + 2] > 3 * 75; // background is ~20-50
        }
    }

    private static bool IsRedAt(Frame frame, int x, int y)
    {
        var px = frame.Pixels;
        var o = y * frame.Stride + x * Frame.BytesPerPixel;
        return IsRed(px[o + 2], px[o + 1], px[o]);
    }

    private static bool RedNear(bool[] red, int w, int h, int x, int y)
    {
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx < w && ny < h && red[ny * w + nx])
                    return true;
            }
        return false;
    }
}
