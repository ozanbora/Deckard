using Deckard.Core.Capture;

namespace Deckard.Perception.Tooltips;

/// <summary>
/// Finds Diablo IV item tooltips in a frame. A tooltip is framed by thin bronze vertical borders about a third of
/// the screen height apart; the game world and UI rarely produce long vertical runs of that color.
/// Works on pixels only, so it scales with resolution and needs no templates.
/// </summary>
public sealed class TooltipLocator
{
    /// <summary>Tooltip width relative to frame height, measured at 3440x1440 (491 px).</summary>
    public double MinWidthRatio { get; init; } = 0.28;
    public double MaxWidthRatio { get; init; } = 0.42;

    /// <summary>A border must run at least this fraction of the frame height.</summary>
    public double MinBorderRatio { get; init; } = 0.15;

    public IReadOnlyList<PixelRect> Locate(Frame frame)
    {
        var borders = FindBorderColumns(frame);
        var found = new List<PixelRect>();
        foreach (var left in borders)
        {
            Border? best = null;
            foreach (var right in borders)
            {
                var width = right.X - left.X;
                if (width < frame.Height * MinWidthRatio || width > frame.Height * MaxWidthRatio)
                    continue;
                var overlap = Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top);
                if (overlap < 0.8 * Math.Min(left.Length, right.Length))
                    continue;
                // Two borders of different tooltips can be a tooltip-width apart too (comparison view); a real
                // tooltip has its dark body just inside both borders.
                var inset = Math.Max(8, frame.Height / 180);
                var top = Math.Max(left.Top, right.Top);
                var bottom = Math.Min(left.Bottom, right.Bottom);
                if (!IsMostlyDark(frame, left.X + inset, top, bottom) || !IsMostlyDark(frame, right.X - inset, top, bottom))
                    continue;
                // Inside its double border a tooltip has no long bronze lines; a lit floor's streaks, one every few
                // pixels, pair into boxes full of them (measured at 3440x1440: dozens, around a comparison).
                var margin = 3 * inset;
                if (borders.Count(b => b.X > left.X + margin && b.X < right.X - margin
                        && Math.Min(b.Bottom, bottom) - Math.Max(b.Top, top) >= 0.3 * (bottom - top)) >= 3)
                    continue;
                if (best is null || right.Length > best.Value.Length)
                    best = right;
            }
            if (best is { } r)
            {
                var top = Math.Min(left.Top, r.Top);
                var bottom = Math.Max(FollowDown(frame, left.X, left.Bottom), FollowDown(frame, r.X, r.Bottom));
                found.Add(new PixelRect(left.X, top, r.X - left.X + 1, bottom - top));
            }
        }

        // The frame has an outer and an inner border line, so each tooltip shows up more than once; merge overlaps. Never
        // past a tooltip's width: a warm-lit floor's streaks pair with a comparison's borders, and merging those made the
        // two tooltips one box. What overlaps a box that much but can't join it is a piece of it, dropped.
        var merged = new List<PixelRect>();
        var maxWidth = frame.Height * MaxWidthRatio;
        foreach (var rect in found.OrderByDescending(r => r.Area))
        {
            bool Overlaps(PixelRect m) => m.Intersect(rect).Area >= 0.6 * Math.Min(m.Area, rect.Area);
            var i = merged.FindIndex(m => Overlaps(m) && m.Union(rect).Width <= maxWidth);
            if (i >= 0)
                merged[i] = merged[i].Union(rect);
            else if (!merged.Any(Overlaps))
                merged.Add(rect);
        }

        // The header banner's border is orange, not bronze, so the detected top sits inside the item name; pad to cover it
        // and the footer below the last border break.
        var bounds = new PixelRect(0, 0, frame.Width, frame.Height);
        var padTop = (int)(frame.Height * TopPaddingRatio);
        var padBottom = (int)(frame.Height * BottomPaddingRatio);
        return [.. merged
            .Select(r => new PixelRect(r.X, r.Y - padTop, r.Width, r.Height + padTop + padBottom).Intersect(bounds))
            .OrderBy(r => r.X)];
    }

    public double TopPaddingRatio { get; init; } = 0.04;
    public double BottomPaddingRatio { get; init; } = 0.08;

    private readonly record struct Border(int X, int Top, int Bottom)
    {
        public int Length => Bottom - Top;
    }

    /// <summary>Longest vertical run of border-colored pixels per column (small gaps allowed), grouped across adjacent columns.</summary>
    private List<Border> FindBorderColumns(Frame frame)
    {
        int width = frame.Width, height = frame.Height;
        var maxGap = Math.Max(3, height / 360);
        var minRun = (int)(height * MinBorderRatio);
        var runStart = new int[width];
        var lastHit = new int[width];
        var bestTop = new int[width];
        var bestBottom = new int[width];
        Array.Fill(lastHit, -1_000_000);

        // Row-major scan keeps memory access sequential.
        var pixels = frame.Pixels;
        for (var y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * frame.Stride, frame.Stride);
            for (var x = 0; x < width; x++)
            {
                var i = x * Frame.BytesPerPixel;
                if (!IsBorderColor(row[i + 2], row[i + 1], row[i]) && !IsCommonBorderColor(row[i + 2], row[i + 1], row[i]))
                    continue;
                if (y - lastHit[x] > maxGap)
                    runStart[x] = y;
                lastHit[x] = y;
                if (y - runStart[x] > bestBottom[x] - bestTop[x])
                {
                    bestTop[x] = runStart[x];
                    bestBottom[x] = y;
                }
            }
        }

        var borders = new List<Border>();
        for (var x = 0; x < width; x++)
        {
            if (bestBottom[x] - bestTop[x] < minRun)
                continue;
            var current = new Border(x, bestTop[x], bestBottom[x]);
            // Borders are a few pixels thick; merge neighbouring columns into one border.
            if (borders.Count > 0 && x - borders[^1].X <= 3)
            {
                var previous = borders[^1];
                if (current.Length > previous.Length)
                    borders[^1] = current;
                continue;
            }
            borders.Add(current);
        }
        return borders;
    }

    /// <summary>
    /// Some frames fade toward the bottom (a legendary chest's goes from RGB 66,54,28 to 36,26,18), below the bronze
    /// test: follows the border down while it stays a dim warm line.
    /// </summary>
    private static int FollowDown(Frame frame, int x, int bottom)
    {
        var maxGap = Math.Max(3, frame.Height / 360);
        var pixels = frame.Pixels;
        for (int y = bottom + 1, gap = 0; y < frame.Height && gap <= maxGap; y++)
        {
            var i = y * frame.Stride + x * Frame.BytesPerPixel;
            byte r = pixels[i + 2], g = pixels[i + 1], b = pixels[i];
            if (r is >= 30 and <= 210 && r >= g && g >= b && r - b >= 12)
                (bottom, gap) = (y, 0);
            else
                gap++;
        }
        return bottom;
    }

    /// <summary>True when most pixels of the column segment are dark (tooltip body; text and icons are sparse near the edge).</summary>
    private static bool IsMostlyDark(Frame frame, int x, int top, int bottom)
    {
        if (x < 0 || x >= frame.Width || bottom <= top)
            return false;
        var pixels = frame.Pixels;
        int dark = 0, total = 0;
        for (var y = top; y < bottom; y += 2)
        {
            var i = y * frame.Stride + x * Frame.BytesPerPixel;
            total++;
            if (Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) < 70)
                dark++;
        }
        return dark >= total * 0.6;
    }

    /// <summary>
    /// A common (white) item's frame, grey-teal: measured RGB(46,52,48) to (61,76,77) on a 3440x1440 capture (its right
    /// side textured, nearly grey), green and blue alike and above red. The same long-border, width and dark-body checks
    /// keep the world's blues out.
    /// </summary>
    internal static bool IsCommonBorderColor(byte r, byte g, byte b) =>
        g is >= 45 and <= 110 && g >= r + 4 && Math.Abs(b - g) <= 12;

    /// <summary>Bronze frame color, roughly RGB(100, 80, 50): warm, mid-bright, not grey and not orange.</summary>
    internal static bool IsBorderColor(byte r, byte g, byte b) =>
        r is >= 70 and <= 210 && r >= g && g >= b && r - b >= 35 && g - b >= 15 && r * 10 <= g * 16;
}
