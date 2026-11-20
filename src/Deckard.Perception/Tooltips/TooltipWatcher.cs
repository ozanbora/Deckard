using Deckard.Core.Capture;

namespace Deckard.Perception.Tooltips;

/// <summary>
/// Reports each tooltip once: when tooltips appear, settle (same look on two checks in a row, so a fade-in is
/// not caught halfway) and differ from the last reported ones. Hovering the same item keeps quiet; a different item
/// or a moved tooltip counts as new.
/// </summary>
public sealed class TooltipWatcher(TooltipLocator locator)
{
    private const int SignatureColumns = 16;
    private const int SignatureRows = 32;

    private Signature[]? _pending;
    private Signature[]? _reported;
    private Signature[] _last = []; // every box of the previous check, wobbling ones too
    private bool _shown; // a reported tooltip is still the one on screen

    /// <summary>A grid cell counts as changed when its brightness differs by more than this (0..255).</summary>
    public int CellThreshold { get; init; } = 24;

    /// <summary>
    /// Two tooltips differ when more than this many of the 16x32 cells changed. Different text changes dozens of cells;
    /// the animated header and item icon change only a few.
    /// </summary>
    public int ChangedCellLimit { get; init; } = 12;

    /// <summary>
    /// Set by the check that sees a different tooltip settle in place of the one last reported, before it's read: what was
    /// shown about the old one is out of date. A tooltip that only closes doesn't set it (the player may be on the card).
    /// </summary>
    public bool Replaced { get; private set; }

    private readonly List<Signature> _ignored = [];

    /// <summary>
    /// A reported box that held no tooltip after all (the game's world framed by bronze-coloured posts): left out from
    /// now on while its content stays the same, even as its located edges jump about (by 100 px, frame to frame), which
    /// otherwise counted as a new tooltip each second and hid the item card. A tooltip opening there changes the content.
    /// </summary>
    public void Ignore(PixelRect rect)
    {
        if (_reported?.FirstOrDefault(s => Overlaps(s.Bounds, rect)) is not { } signature)
            return;
        _ignored.Add(signature);
        _reported = [.. _reported.Where(s => s != signature)];
    }

    /// <summary>Returns the tooltip bounds when a new, settled set of tooltips is visible; otherwise an empty list.</summary>
    public IReadOnlyList<PixelRect> Check(Frame frame)
    {
        Replaced = false;
        var located = locator.Locate(frame);
        if (located.Count == 0)
            _ignored.Clear();
        // The located edges of a tooltip that holds still flip by a few pixels (2165 or 2171 for the same frame content,
        // measured on a set charm's comparison), and a grid laid 6 px aside reads its border column differently: 20 cells
        // changed, another tooltip by the count. So a box near one already seen is measured where that one was.
        var seen = (_reported ?? []).Concat(_pending ?? []).ToList();
        var all = located.Select(r => Signature.Of(frame, seen.FirstOrDefault(s => Signature.Near(s.Bounds, r))?.Bounds ?? r))
            .Where(s => !_ignored.Any(i => Overlaps(i.Bounds, s.Bounds) && s.ChangedCells(i, CellThreshold) <= ChangedCellLimit))
            .ToArray();
        // A box over the same spot as one of the last check but whose edges jumped further than a tooltip's wobble is the
        // game's world framed by bronze streaks (a lit floor's, beside a comparison: 60 px up and down from check to
        // check): it never settles, and counted in, it kept the tooltips beside it from settling, or hid their card.
        var current = all.Where(c => !_last.Any(p => !p.Near(c) && Overlaps(p.Bounds, c.Bounds))).ToArray();
        _last = all;
        var rects = current.Select(s => s.Bounds).ToList();
        if (rects.Count == 0)
        {
            _pending = null;
            _reported = null; // moving away and back to the same item reports it again
            _shown = false;
            return [];
        }

        if (_reported is not null && Same(current, _reported))
        {
            if (_shown)
                return [];
            // Back to the reported tooltip after another one replaced it (Alt's advanced view, held briefly): the card
            // about it was hidden, so it's reported again.
            (_shown, _pending) = (true, null);
            return rects;
        }
        // Only another box beside the reported tooltips (the game's world, framed for a moment) replaces nothing: it's
        // read once it settles, and what's shown about the tooltips stays up meanwhile.
        if (_shown && (_pending is null || !Same(current, _pending)) && !_reported!.All(r => current.Any(c => Same(c, r))))
        {
            Replaced = true; // another tooltip than the one reported: reported once, on its first sight
            _shown = false;
        }
        if (_pending is not null && Same(current, _pending))
        {
            _reported = current;
            _pending = null;
            _shown = true;
            return rects;
        }
        _pending = current;
        return [];
    }

    private static bool Overlaps(PixelRect a, PixelRect b) => a.Intersect(b).Area >= 0.5 * Math.Min(a.Area, b.Area);

    private bool Same(Signature[] a, Signature[] b) => a.Length == b.Length && a.Zip(b).All(p => Same(p.First, p.Second));

    private bool Same(Signature a, Signature b) => a.Near(b) && a.ChangedCells(b, CellThreshold) <= ChangedCellLimit;

    private sealed record Signature(PixelRect Bounds, byte[] Cells)
    {
        // The located edges wobble by ~10 px when the game's world moves behind them (2017-2027 under a nameplate at
        // 3440x1440), which counted as a new tooltip each second; a tooltip that really moves jumps far further (a bag
        // cell over, ~110 px). The top wobbles most: a lit floor's streaks above it join its border (y 0, 15 or 51 for
        // one still tooltip), so it's held to the sides and bottom, and only loosely to the top.
        public bool Near(Signature other) => Near(Bounds, other.Bounds);

        public static bool Near(PixelRect a, PixelRect b) =>
            Math.Abs(a.X - b.X) <= 24 && Math.Abs(a.Bottom - b.Bottom) <= 24 && Math.Abs(a.Y - b.Y) <= 64;

        public int ChangedCells(Signature other, int threshold) => Cells.Zip(other.Cells).Count(p => Math.Abs(p.First - p.Second) > threshold);

        /// <summary>Block-averaged brightness of the tooltip on a coarse grid; cheap and stable against single-pixel noise.</summary>
        public static Signature Of(Frame frame, PixelRect bounds)
        {
            var cells = new byte[SignatureColumns * SignatureRows];
            var pixels = frame.Pixels;
            for (var row = 0; row < SignatureRows; row++)
            {
                var y0 = bounds.Y + row * bounds.Height / SignatureRows;
                var y1 = bounds.Y + (row + 1) * bounds.Height / SignatureRows;
                for (var col = 0; col < SignatureColumns; col++)
                {
                    var x0 = bounds.X + col * bounds.Width / SignatureColumns;
                    var x1 = bounds.X + (col + 1) * bounds.Width / SignatureColumns;
                    long sum = 0, count = 0;
                    for (var y = y0; y < y1; y += 2)
                        for (var x = x0; x < x1; x += 2)
                        {
                            var i = y * frame.Stride + x * Frame.BytesPerPixel;
                            sum += pixels[i] + pixels[i + 1] + pixels[i + 2];
                            count += 3;
                        }
                    cells[row * SignatureColumns + col] = (byte)(count == 0 ? 0 : sum / count);
                }
            }
            return new Signature(bounds, cells);
        }
    }
}
