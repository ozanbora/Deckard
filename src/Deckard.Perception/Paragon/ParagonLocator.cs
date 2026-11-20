using Deckard.Core.Builds;
using Deckard.Core.Capture;
using Deckard.Perception;
using Deckard.Core.Items;
using Deckard.Perception.Imaging;
using Deckard.Perception.SkillTrees;

namespace Deckard.Perception.Paragon;

/// <summary>Where the in-game paragon grid is drawn: cell (x, y) of the whole paragon has its centre at
/// (OriginX + x * Pitch, OriginY + y * Pitch), cells as in <see cref="PlacedBoard.Cell"/>.</summary>
/// <param name="Attached">Bit i set: the build's board i (in step order) is attached in game and in view.</param>
/// <param name="Layout">Which of the game's paragon screens the grid is on.</param>
/// <param name="Covered">Changes when the attached boards' node cells hidden under something (a game tooltip) or taken by the
/// player change; see <see cref="ParagonLocator.IsCovered"/> and <see cref="ParagonLocator.IsTaken"/>.</param>
/// <param name="Empty">Bit i set: board i's place is in view and empty, so it isn't attached yet.</param>
/// <param name="Selecting">The game's Board Selection panel is open: the player is choosing a board for a gate.</param>
public readonly record struct ParagonPlacement(double OriginX, double OriginY, double Pitch, int Attached, ScreenLayout Layout = ScreenLayout.Full,
    int Covered = 0, int Empty = 0, bool Selecting = false)
{
    public (double X, double Y) ToScreen(int x, int y) => (OriginX + x * Pitch, OriginY + y * Pitch);

    public bool IsAttached(int board) => board < 32 && (Attached & (1 << board)) != 0;

    public bool IsEmpty(int board) => board < 32 && (Empty & (1 << board)) != 0;
}

/// <summary>
/// Finds the open in-game paragon screen and its grid, from pixels only. Node symbols are light grey (~97) on a dark
/// background (under ~50), so the lit pixels repeat at the grid's pitch along both axes: folding their column and row
/// counts by a candidate pitch shows which pitch and phase they line up on. Which cell is which comes from sliding the
/// build's boards (every node cell, taken or not) over the lit cells. The game zooms (measured 45-74 px a cell at 1440p)
/// and pans; the boards are drawn as the build places them, so no rotation is searched.
/// </summary>
public sealed class ParagonLocator
{
    private const int Step = 2;                          // analyse every 2nd pixel each way
    private const double MinPitch = 0.014, MaxPitch = 0.1; // per screen height: zoomed out past 45 px, zoomed in past 74 px at 1440p
    private const double Window = 0.2;                   // a node symbol sits within ±0.2 pitch of its cell's centre
    public const double MinFit = 0.6;                    // share of attached boards' node cells and lit cells that agree
    private const double AttachedShare = 0.5;            // a board in view with fewer of its node cells lit isn't attached yet
    private const int AttachedMinCells = 8;              // fewer cells of a newly seen board say too little: two once sat on the search box's text

    private readonly (int X, int Y)[][] _boards; // in step order, as ParagonPlacement.Attached numbers them
    private ParagonPlacement? _last;

    /// <param name="boards">Every node cell of each of the build's boards (see <see cref="Cells"/>).</param>
    public ParagonLocator(IEnumerable<IEnumerable<(int X, int Y)>> boards)
    {
        _boards = [.. boards.Select(b => b.ToArray())];
    }

    /// <summary>Every node cell (taken by the build or not) of each board in a paragon step, in step order (a board the
    /// season's data doesn't have has none).</summary>
    public static IEnumerable<IEnumerable<(int X, int Y)>> Cells(ParagonStep step, ParagonData paragon) =>
        step.Boards.Select(board => paragon.Boards.GetValueOrDefault(board.Id) is { } data
            ? Enumerable.Range(0, data.Nodes.Count).Where(i => data.Nodes[i] is not null).Select(i => board.Cell(i, data.Width))
            : []);

    /// <summary>
    /// The grid area. Full screen: right of the stats and glyphs panel, between the top bar (tabs) and the bottom bar.
    /// Docked: the right-hand panel between its attribute row and its glyph strip (measured at 3440x1440: x 2554-3411,
    /// y 310-1138).
    /// </summary>
    public static PixelRect Panel(int width, int height, ScreenLayout layout = ScreenLayout.Full) => layout == ScreenLayout.Full
        ? new((int)(height * 0.4), (int)(height * 0.07), width - (int)(height * 0.4), (int)(height * 0.86))
        : new(width - (int)(height * 0.615), (int)(height * 0.215), (int)(height * 0.595), (int)(height * 0.575));

    /// <summary>
    /// The game's Keyword Search box over the full screen's grid, top right: its grey text isn't nodes, and no mark goes
    /// over it. Measured at 3440x1440: x 2875-3405, y 128-192. Docked, the box sits above the grid area: none.
    /// </summary>
    public static PixelRect SearchBox(int width, int height, ScreenLayout layout = ScreenLayout.Full) => layout == ScreenLayout.Full
        ? new(width - (int)(height * 0.4), (int)(height * 0.085), (int)(height * 0.385), (int)(height * 0.05))
        : default;

    /// <summary>Which paragon screen is open, by its "Paragon" tab lit red; null when neither is.</summary>
    public static ScreenLayout? Layout(Frame frame)
    {
        // Measured at 3440x1440. Full screen: the lit tab spans x 1740-1855 (centre + 0.056h) and y 40-83. Docked: x
        // 2990-3110 (right edge - 0.271h) and y 124-172.
        // Each on its dark panel: a red-lit world once passed for a lit tab alone. Docked: the panel's header and frame,
        // measured RGB ~20,25,25 at 0.3-0.58h from the right edge.
        var h = frame.Height;
        if (TabLit(frame, frame.Width / 2 + (int)(h * 0.056), (int)(h * 0.034), (int)(h * 0.052)) && SkillTreeLocator.TabBarDark(frame))
            return ScreenLayout.Full;
        // And only a tab's width of red: the Character panel's backdrop behind the doll is red at those rows from x
        // 2920 to 3300, and passed for the lit tab (its stats and inventory then drew as a paragon board).
        if (TabLit(frame, frame.Width - (int)(h * 0.271), (int)(h * 0.092), (int)(h * 0.112))
            && !TabLit(frame, frame.Width - (int)(h * 0.2), (int)(h * 0.092), (int)(h * 0.112))
            && ImageOps.IsDarkPanel(frame, frame.Width - (int)(h * 0.42), (int)(h * 0.025))
            && ImageOps.IsDarkPanel(frame, frame.Width - (int)(h * 0.58), (int)(h * 0.05))
            && ImageOps.IsDarkPanel(frame, frame.Width - (int)(h * 0.3), (int)(h * 0.05)))
            return ScreenLayout.Docked;
        return null;
    }

    public static bool IsOpen(Frame frame) => Layout(frame) is not null;

    private readonly List<PixelRect> _masked = [];

    /// <summary>The game's panels over the grid in the last frame located (search box, preview banner, a tooltip): no marks there.</summary>
    public IReadOnlyList<PixelRect> Masked => _masked;

    /// <summary>
    /// Board Preview Mode (attaching a board) has Attach Board, Rotate Board and Cancel at the bottom; the first lit red
    /// where the normal screen is dark. Measured at 3440x1440: Attach Board around (1514, 1400).
    /// </summary>
    public static bool InPreview(Frame frame) =>
        ButtonLit(frame, frame.Width / 2 - (int)(frame.Height * 0.143), (int)(frame.Height * 0.972));

    /// <summary>
    /// The Board Selection panel (choosing the board for a gate) is open on the right, with Preview and Close lit red at
    /// its bottom. Measured at 3440x1440: Preview around (2896, 1256), Close around (3096, 1256).
    /// </summary>
    public static bool InBoardSelection(Frame frame) =>
        ButtonLit(frame, frame.Width - (int)(frame.Height * 0.378), (int)(frame.Height * 0.872))
        && ButtonLit(frame, frame.Width - (int)(frame.Height * 0.239), (int)(frame.Height * 0.872));

    /// <summary>The Board Selection panel. Measured at 3440x1440: x 2580-3395, y 115-1335.</summary>
    public static PixelRect BoardSelection(int width, int height) =>
        new(width - (int)(height * 0.6), (int)(height * 0.08), (int)(height * 0.57), (int)(height * 0.85));

    /// <summary>A red game button around (cx, cy): red over a good share of it, its lettering covering the rest.</summary>
    private static bool ButtonLit(Frame frame, int cx, int cy)
    {
        var h = frame.Height;
        int rx = (int)(h * 0.015), ry = (int)(h * 0.006);
        int lit = 0, total = 0;
        var px = frame.Pixels;
        for (var y = Math.Max(0, cy - ry); y <= Math.Min(frame.Height - 1, cy + ry); y++)
            for (var x = Math.Max(0, cx - rx); x <= Math.Min(frame.Width - 1, cx + rx); x += 2)
            {
                var o = y * frame.Stride + x * Frame.BytesPerPixel;
                int b = px[o], g = px[o + 1], r = px[o + 2];
                total++;
                if (r > 45 && r > 3 * g && r > 3 * b)
                    lit++;
            }
        return lit > total * 0.25;
    }

    /// <summary>Board Preview Mode's "BOARD PREVIEW MODE / Board Limit" banner, top centre. Measured at 3440x1440: x 1400-2080, y 140-236.</summary>
    public static PixelRect PreviewBanner(int width, int height) =>
        new(width / 2 - (int)(height * 0.24), (int)(height * 0.09), (int)(height * 0.48), (int)(height * 0.08));

    /// <summary>
    /// A game tooltip over the grid (a node's, while points are spent), in analysis cells: a nearly black panel whose
    /// thin grey-green border and separators make long straight rows of edge pixels (see <see cref="LitMask"/>), which
    /// node symbols never do (each is under a cell wide). The box spans the long rows when there are at least two, apart
    /// (a top and a bottom edge).
    /// </summary>
    internal static PixelRect? TooltipBox(bool[] lit, int w, int h, int frameHeight)
    {
        var minRun = (int)(frameHeight * 0.08 / Step);
        var runs = new List<(int Left, int Right, int Y)>();
        // The frame is a band of textured stone (about 6 px of it this colour at 1440p): a column counts when any of three
        // rows has it, and a run bridges gaps of up to 3 samples.
        bool Edge(int x, int y) => lit[y * w + x] || y + 1 < h && lit[(y + 1) * w + x] || y + 2 < h && lit[(y + 2) * w + x];

        // How far down the frame's side goes from the top edge at column x (a few samples either side, small gaps bridged).
        int Side(int x, int from)
        {
            int last = from, gap = 0;
            for (var y = from; y < h && gap <= 6; y++)
            {
                var seen = false;
                for (var dx = -4; dx <= 4 && !seen; dx++)
                    seen = x + dx >= 0 && x + dx < w && lit[y * w + x + dx];
                if (seen)
                    (last, gap) = (y, 0);
                else
                    gap++;
            }
            return last;
        }
        for (var y = 0; y < h; y++)
        {
            int start = -1, gap = 0;
            for (var x = 0; x <= w; x++)
            {
                if (x < w && Edge(x, y))
                {
                    if (start < 0)
                        start = x;
                    gap = 0;
                    continue;
                }
                if (start >= 0 && x < w && ++gap <= 3)
                    continue; // a gap in the frame's texture
                if (start >= 0 && x - gap - start >= minRun)
                    runs.Add((start, x - gap, y));
                start = -1;
                gap = 0;
            }
        }
        // The top edge is the reliable part: the node's medallion sits on it and breaks it in two, so pieces on the same
        // rows join across a medallion-sized gap; the bottom (the Refund bar) is another colour. A tooltip is wide (a node's
        // about 0.47 of the height at 1440p), and its frame's sides run down from the top edge's ends to its bottom.
        var join = (int)(frameHeight * 0.1 / Step);
        (int Left, int Right, int Top, int Bottom)? best = null;
        foreach (var y in runs.Select(r => r.Y).Distinct())
        {
            var row = runs.Where(r => r.Y == y).OrderBy(r => r.Left).ToList();
            int edgeLeft = row[0].Left, edgeRight = row[0].Right;
            for (var i = 1; i <= row.Count; i++)
            {
                if (i < row.Count && row[i].Left - edgeRight <= join)
                {
                    edgeRight = Math.Max(edgeRight, row[i].Right);
                    continue;
                }
                if (edgeRight - edgeLeft >= frameHeight * 0.25 / Step)
                {
                    var reach = Math.Max(Side(edgeLeft, y), Side(edgeRight, y));
                    if (reach - y >= frameHeight * 0.06 / Step && (best is null || reach - y > best.Value.Bottom - best.Value.Top))
                        best = (edgeLeft, edgeRight, y, reach);
                }
                if (i < row.Count)
                    (edgeLeft, edgeRight) = (row[i].Left, row[i].Right);
            }
        }
        if (best is not var (left, right, top, bottom))
            return null;
        // The top edge's ends fall short of the frame's carved corners (measured: 40-50 px at 1440p).
        int pad = 3, side = (int)(frameHeight * 0.035 / Step);
        int x0 = Math.Max(0, left - side), y0 = Math.Max(0, top - pad);
        return new PixelRect(x0, y0, Math.Min(w, right + side) - x0, Math.Min(h, bottom + pad + 1) - y0);
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
                if (r > 30 && r > 3 * g && r > 3 * b) // dimmed to ~40 while the Board Selection panel is open; an unlit tab is grey
                    lit++;
            }
        return lit > total * 0.4;
    }

    /// <summary>The grid's placement, or null when the paragon screen isn't open or the build's boards don't fit it.</summary>
    public ParagonPlacement? Locate(Frame frame)
    {
        if (_boards.All(b => b.Length == 0) || Layout(frame) is not { } layout)
        {
            _seenAttached = 1; // closed: boards and nodes may be refunded before it opens again
            _taken.Clear();
            return _last = null;
        }
        if (_last is { } previous && previous.Layout != layout)
            _last = null; // the other screen: its grid isn't where this one's was
        var panel = Panel(frame.Width, frame.Height, layout);
        int w = panel.Width / Step, h = panel.Height / Step;
        // The game's own panels over the grid read as no nodes, and no marks go over them.
        _masked.Clear();
        _masked.Add(SearchBox(frame.Width, frame.Height, layout));
        if (InPreview(frame))
            _masked.Add(PreviewBanner(frame.Width, frame.Height));
        var selecting = layout == ScreenLayout.Full && InBoardSelection(frame);
        if (selecting)
            _masked.Add(BoardSelection(frame.Width, frame.Height));
        var lit = LitMask(frame, panel, _masked, w, h, out var edges);
        if (TooltipBox(edges, w, h, frame.Height) is { } box)
        {
            for (var y = box.Y; y < box.Bottom; y++)
                Array.Clear(lit, y * w + box.X, box.Width);
            _masked.Add(new PixelRect(panel.X + box.X * Step, panel.Y + box.Y * Step, box.Width * Step, box.Height * Step));
        }
        var columns = new double[w];
        var rows = new double[h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (lit[y * w + x])
                {
                    columns[x]++;
                    rows[y]++;
                }
        // While tracking, the zoom changes only a little between frames: search near the last pitch first, where a weaker
        // pattern is enough (a game tooltip's borders and text over the board blur it).
        var near = _last is { } seen ? Lattice(columns, rows, seen.Pitch / Step * 0.9, seen.Pitch / Step * 1.1, minScore: 0.3) : null;
        if ((near ?? Lattice(columns, rows, MinPitch * frame.Height / Step, MaxPitch * frame.Height / Step, minScore: 1.0)) is not var (pitch, phaseX, phaseY))
            return _last = null;
        // The pitch estimate wobbles by a percent or so on a still screen: within that, it's the last pitch, and only the
        // phase is measured again, so a still board stays exactly where it was.
        if (_last is { } held && Math.Abs(held.Pitch / Step - pitch) < pitch * 0.02)
        {
            pitch = held.Pitch / Step;
            phaseX = Fold(columns, pitch).Phase;
            phaseY = Fold(rows, pitch).Phase;
        }
        // Back to frame pixels: the first grid line at or past the panel's top-left corner. Grid columns and rows are
        // numbered from there; a build cell (x, y) at offset (dx, dy) sits on grid column x + dx, row y + dy.
        pitch *= Step;
        double firstX = panel.X + phaseX * Step, firstY = panel.Y + phaseY * Step;
        int columnCount = (int)Math.Ceiling((panel.Right - firstX) / pitch), rowCount = (int)Math.Ceiling((panel.Bottom - firstY) / pitch);
        var litCells = LitCells(lit, w, h, panel, pitch, firstX, firstY, columnCount, rowCount);
        var litCount = litCells.Count(c => c);
        if (litCount < (_last is null ? 8 : 3)) // finding the grid takes a few nodes; following a drag nearly off screen, fewer
            return _last = null;
        (double Fit, int Boards) Fit(int dx, int dy) => Agreement(litCells, litCount, columnCount, rowCount, dx, dy, _seenAttached | (_last?.Attached ?? 0));
        ParagonPlacement At(int dx, int dy, int boards) => new(firstX + dx * pitch, firstY + dy * pitch, pitch, boards, layout);

        // Fast path: the same pitch as last time, so the grid moved by a drag. The cells nearest their last place are only
        // a guess: a drag of over half a cell between two analysed frames rounds to the neighbouring cell, and a dense
        // board one row off still fits well. So the offsets around the guess compete, and the best fit wins.
        if (_last is { } last && Math.Abs(last.Pitch - pitch) < pitch * 0.02)
        {
            int guessX = (int)Math.Round((last.OriginX - firstX) / pitch), guessY = (int)Math.Round((last.OriginY - firstY) / pitch);
            var (dx, dy, kept) = (guessX, guessY, Fit(guessX, guessY));
            for (var oy = -2; oy <= 2; oy++)
                for (var ox = -2; ox <= 2; ox++)
                    if (Fit(guessX + ox, guessY + oy) is var fit && fit.Fit > kept.Fit)
                        (dx, dy, kept) = (guessX + ox, guessY + oy, fit);
            var moved = At(dx, dy, kept.Boards);
            var (cx, cy) = (panel.X + panel.Width / 2.0, panel.Y + panel.Height / 2.0);
            double Shift(ParagonPlacement p) => Math.Max(Math.Abs((cx - p.OriginX) / p.Pitch * moved.Pitch + moved.OriginX - cx), Math.Abs((cy - p.OriginY) / p.Pitch * moved.Pitch + moved.OriginY - cy));
            var still = Shift(last) <= Math.Max(3, pitch * 0.06);
            // A still grid's fit wobbles by a pixel or two (the analysis grid, the embers): keep the shown placement.
            if (kept.Fit >= MinFit)
                return Remember(still ? last with { Attached = kept.Boards } : moved);
            // A game tooltip over the board leaves the nodes under it dark and its text lit, which drags the fit down; a grid
            // that hasn't moved is still the same board, so it keeps its place (the covered nodes lose their marks).
            if (still && kept.Fit >= MinFit / 2)
                return Remember(last);
        }

        // Every way to line a build cell up with a lit cell; the best agreement wins.
        var best = (Fit: 0.0, X: 0, Y: 0, Boards: 0);
        var tried = new HashSet<(int, int)>();
        for (var gy = 0; gy < rowCount; gy++)
            for (var gx = 0; gx < columnCount; gx++)
            {
                if (!litCells[gy * columnCount + gx])
                    continue;
                foreach (var board in _boards)
                    foreach (var (cx, cy) in board)
                    {
                        if (!tried.Add((gx - cx, gy - cy)))
                            continue;
                        var (fit, boards) = Fit(gx - cx, gy - cy);
                        if (fit > best.Fit)
                            best = (fit, gx - cx, gy - cy, boards);
                    }
            }
        return best.Fit >= MinFit ? Remember(At(best.X, best.Y, best.Boards)) : _last = null;

        // Notes which attached-board node cells are dark, so covered by something (every node cell of an attached board
        // shows its symbol, taken or not), and keys the placement by them so a tooltip coming or going counts as a change.
        ParagonPlacement Remember(ParagonPlacement placement)
        {
            int ox = (int)Math.Round((placement.OriginX - firstX) / pitch), oy = (int)Math.Round((placement.OriginY - firstY) / pitch);
            // A board whose place is in view and dark isn't attached: never attached yet, or detached again.
            var current = placement.Attached | 1;
            var empty = 0;
            for (var b = 0; b < _boards.Length && b < 32; b++)
            {
                if ((current & (1 << b)) != 0)
                    continue;
                int inView = 0, hits = 0;
                foreach (var (x, y) in _boards[b])
                {
                    int gx = x + ox, gy = y + oy;
                    if (gx < 0 || gy < 0 || gx >= columnCount || gy >= rowCount)
                        continue;
                    inView++;
                    if (litCells[gy * columnCount + gx])
                        hits++;
                }
                if (inView >= AttachedMinCells && hits < inView * AttachedShare)
                    empty |= 1 << b;
            }
            // A board seen attached stays attached while the screen is open, also once scrolled out of view, and the start
            // board always is, until its place is seen empty (detached). Only a board whose place is in view and empty may
            // be attached next.
            _seenAttached = (_seenAttached & ~empty) | current;
            // The build lists its boards in the order they're attached, so a later board seen attached means the earlier
            // ones are too, also those not seen since Deckard started.
            // shortcut: assumes the planner's order is the attaching order; a build that branches off and attaches out of
            // order would want the gates read instead.
            _seenAttached |= (int)((1L << (32 - System.Numerics.BitOperations.LeadingZeroCount((uint)_seenAttached))) - 1);
            _seenAttached &= ~empty;
            placement = placement with { Attached = _seenAttached };
            _covered.Clear();
            var key = 0;
            for (var b = 0; b < _boards.Length; b++)
            {
                if (!placement.IsAttached(b))
                    continue;
                foreach (var (x, y) in _boards[b])
                {
                    int gx = x + ox, gy = y + oy;
                    if (gx < 0 || gy < 0 || gx >= columnCount || gy >= rowCount)
                        continue;
                    // A covered node keeps what was seen of it (a tooltip hides a taken node's red ring, and its text reads
                    // lit); one in plain view is taken or not as it looks now, so a refund shows too.
                    if (!litCells[gy * columnCount + gx])
                    {
                        _covered.Add((x, y));
                        key = HashCode.Combine(key, x, y);
                    }
                    else if (TakenAt(frame, firstX + gx * pitch, firstY + gy * pitch, pitch))
                        _taken.Add((x, y));
                    else
                        _taken.Remove((x, y));
                    if (_taken.Contains((x, y)))
                        key = HashCode.Combine(key, -x, -y);
                }
            }
            var remembered = placement with { Covered = key, Empty = empty, Selecting = selecting };
            _last = remembered;
            return remembered;
        }
    }

    private readonly HashSet<(int X, int Y)> _covered = [];
    private readonly HashSet<(int X, int Y)> _taken = [];
    private int _seenAttached = 1; // boards seen attached since the screen opened (the start board always is)

    /// <summary>The boards seen attached since the paragon screen opened (bit i: board i), also when the grid isn't found now.</summary>
    public int SeenAttached => _seenAttached;

    /// <summary>
    /// Whether the player has taken a node cell of an attached board: as it looks in the last frame located, or, while
    /// something covers it, as it last looked.
    /// </summary>
    public bool IsTaken(int x, int y) => _taken.Contains((x, y));

    /// <summary>
    /// A taken node has a red ring around its symbol: measured on a 3440x1440 capture, 5-35% red pixels on a ring from 0.3
    /// to 0.45 of a cell around the centre on taken nodes, at most 2% on the others.
    /// </summary>
    private static bool TakenAt(Frame frame, double cx, double cy, double pitch)
    {
        int n = 0, red = 0;
        var px = frame.Pixels;
        for (var a = 0; a < 48; a++)
        {
            double cos = Math.Cos(a * Math.PI / 24), sin = Math.Sin(a * Math.PI / 24);
            for (var r = pitch * 0.3; r <= pitch * 0.45; r += Math.Max(1.5, pitch * 0.03))
            {
                int x = (int)(cx + r * cos), y = (int)(cy + r * sin);
                if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height)
                    continue;
                var o = y * frame.Stride + x * Frame.BytesPerPixel;
                int b = px[o], g = px[o + 1], rr = px[o + 2];
                n++;
                if (rr > 60 && rr > 2 * g && rr > 2 * b)
                    red++;
            }
        }
        return n > 0 && red >= n * 0.035;
    }

    /// <summary>
    /// Whether a node cell of an attached board was dark in the last frame located: something (a game tooltip) is over it,
    /// so no mark should be drawn there.
    /// </summary>
    public bool IsCovered(int x, int y) => _covered.Contains((x, y));

    /// <summary>
    /// Agreement between the attached boards' node cells in view and the lit cells, both ways (Jaccard): lit nodes the
    /// build doesn't have count against it, as do the attached boards' node cells left dark. A board in view with most of
    /// its cells dark isn't attached yet (the build's later boards) and is left out.
    /// </summary>
    private (double Fit, int Boards) Agreement(bool[] litCells, int litCount, int columns, int rows, int dx, int dy, int wasAttached)
    {
        int inView = 0, hits = 0, attached = 0;
        for (var b = 0; b < _boards.Length; b++)
        {
            int boardInView = 0, boardHits = 0;
            foreach (var (cx, cy) in _boards[b])
            {
                int gx = cx + dx, gy = cy + dy;
                if (gx < 0 || gy < 0 || gx >= columns || gy >= rows)
                    continue;
                boardInView++;
                if (litCells[gy * columns + gx])
                    boardHits++;
            }
            // The start board is always attached; another needs enough cells in view, unless it was attached a frame ago
            // (a board dragged nearly off screen keeps its marks).
            var enough = b == 0 || boardInView >= AttachedMinCells || b < 32 && (wasAttached & (1 << b)) != 0;
            if (boardInView > 0 && enough && boardHits >= boardInView * AttachedShare)
            {
                inView += boardInView;
                hits += boardHits;
                if (b < 32)
                    attached |= 1 << b;
            }
        }
        var union = inView + litCount - hits;
        return (union == 0 ? 0 : (double)hits / union, attached);
    }

    /// <summary>Grid cells (row by row from the first grid line inside the panel) with a node symbol at their centre.</summary>
    private static bool[] LitCells(bool[] lit, int w, int h, PixelRect panel, double pitch, double firstX, double firstY, int columns, int rows)
    {
        var cells = new bool[columns * rows];
        var reach = Math.Max(1, (int)(pitch * Window / Step));
        var need = Math.Max(2, reach * reach / 4); // a symbol covers a good part of its cell's centre
        for (var j = 0; j < rows; j++)
            for (var i = 0; i < columns; i++)
            {
                int mx = (int)((firstX + i * pitch - panel.X) / Step), my = (int)((firstY + j * pitch - panel.Y) / Step), count = 0;
                for (var y = Math.Max(0, my - reach); y <= Math.Min(h - 1, my + reach); y++)
                    for (var x = Math.Max(0, mx - reach); x <= Math.Min(w - 1, mx + reach); x++)
                        if (lit[y * w + x])
                            count++;
                cells[j * columns + i] = count >= need;
            }
        return cells;
    }

    /// <summary>
    /// The pitch (analysis pixels) and the phase of the grid's lines along each axis. For each candidate pitch the
    /// profiles are folded onto one period; nodes make a peak there. Contrast (mean inside the ±Window band minus mean
    /// outside, over the overall mean) is highest at the true pitch: at half the pitch every other band is empty, at
    /// twice the pitch the band is wider than the peak and the other nodes fall outside it.
    /// </summary>
    internal static (double Pitch, double PhaseX, double PhaseY)? Lattice(double[] columns, double[] rows, double minPitch, double maxPitch, double minScore)
    {
        var best = (Score: 0.0, Pitch: 0.0, X: 0.0, Y: 0.0);
        for (var pitch = minPitch; pitch <= maxPitch; pitch *= 1.005)
        {
            var (cx, px) = Fold(columns, pitch);
            var (cy, py) = Fold(rows, pitch);
            if (cx + cy > best.Score)
                best = (cx + cy, pitch, px, py);
        }
        return best.Score > minScore ? (best.Pitch, best.X, best.Y) : null; // measured: real grids score 3-6, empty screens under 0.5
    }

    private static (double Contrast, double Phase) Fold(double[] profile, double pitch)
    {
        var bins = Math.Max(4, (int)Math.Round(pitch));
        var sums = new double[bins];
        var counts = new int[bins];
        double total = 0;
        for (var i = 0; i < profile.Length; i++)
        {
            var bin = (int)(i % pitch / pitch * bins) % bins;
            sums[bin] += profile[i];
            counts[bin]++;
            total += profile[i];
        }
        if (total == 0)
            return (0, 0);
        var mean = total / profile.Length;
        var half = Math.Max(1, (int)Math.Round(bins * Window));
        var allCounts = counts.Sum();
        // The band around each centre, slid round the period one bin at a time.
        double inside = 0;
        var nIn = 0;
        for (var b = -half; b <= half; b++)
        {
            inside += sums[(b + bins) % bins];
            nIn += counts[(b + bins) % bins];
        }
        var best = (Contrast: double.MinValue, Phase: 0.0);
        for (var centre = 0; centre < bins; centre++)
        {
            var nOut = allCounts - nIn;
            if (nIn > 0 && nOut > 0)
            {
                var contrast = (inside / nIn - (total - inside) / nOut) / mean;
                if (contrast > best.Contrast)
                    best = (contrast, (centre + 0.5) * pitch / bins);
            }
            int leaving = (centre - half + bins) % bins, entering = (centre + half + 1) % bins;
            inside += sums[entering] - sums[leaving];
            nIn += counts[entering] - counts[leaving];
        }
        return best;
    }

    /// <summary>
    /// Light grey pixels (node symbols), sampled every Step pixels over the panel, outside the masked areas; red frame and
    /// embers aren't grey. <paramref name="edges"/> are the game tooltip border's grey-green (measured (45,59,54) to
    /// (67,84,80): green and blue alike and above red, where node symbols are neutral grey or clearly blue and the dark
    /// background is too dark).
    /// </summary>
    private static bool[] LitMask(Frame frame, PixelRect panel, IReadOnlyList<PixelRect> masked, int w, int h, out bool[] edges)
    {
        var mask = new bool[w * h];
        edges = new bool[w * h];
        var px = frame.Pixels;
        for (var y = 0; y < h; y++)
        {
            var row = (panel.Y + y * Step) * frame.Stride;
            int fy = panel.Y + y * Step;
            var rowMasks = masked.Where(m => fy >= m.Y && fy < m.Bottom).ToArray();
            for (var x = 0; x < w; x++)
            {
                int fx = panel.X + x * Step;
                if (rowMasks.Length > 0 && rowMasks.Any(m => fx >= m.X && fx < m.Right))
                    continue;
                var o = row + fx * Frame.BytesPerPixel;
                int b = px[o], g = px[o + 1], r = px[o + 2];
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                mask[y * w + x] = max >= 70 && max - min <= 60;
                edges[y * w + x] = max >= 45 && g >= r + 8 && Math.Abs(b - g) <= 10 && max - min <= 40;
            }
        }
        return mask;
    }
}
