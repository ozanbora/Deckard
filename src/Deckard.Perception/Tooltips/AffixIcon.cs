using Deckard.Core.Capture;

namespace Deckard.Perception.Tooltips;

/// <summary>
/// Tells a greater affix by the icon left of its line: a star about 12-14 px square at 1080p (peach, magenta or
/// blue-violet), where a regular affix has a 7 px diamond, a tempered one a small swirl, a fixed one a 9x15 shield and a
/// socketed gem a 23 px picture.
/// </summary>
public static class AffixIcon
{
    // Relative to the tooltip's width (367 px at 1080p), which scales with the screen: the icon column runs from 8 to
    // 38 px right of the border, a star 10 to 18 px, a row 26 px.
    private const double ColumnStart = 0.022, ColumnEnd = 0.104, MinStar = 0.027, MaxStar = 0.05, Row = 0.07;

    public static bool IsGreater(Frame frame, PixelRect tooltip, PixelRect line)
    {
        var w = tooltip.Width;
        int x0 = tooltip.X + (int)(w * ColumnStart), x1 = Math.Min(frame.Width, tooltip.X + (int)(w * ColumnEnd));
        if (line.X > x0 + w / 35)
            x1 = Math.Min(x1, line.X - 1); // stop short of the text (unless OCR took the icon in as a character)
        // The icon sits on the line's first row, even when its text wrapped.
        int y0 = Math.Max(0, line.Y - 2), y1 = Math.Min(frame.Height, line.Y + Math.Min(line.Height, (int)(w * Row)) + 2);
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        var pixels = frame.Pixels;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var i = y * frame.Stride + x * Frame.BytesPerPixel;
                if (Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) <= 110)
                    continue;
                (minX, maxX, minY, maxY) = (Math.Min(minX, x), Math.Max(maxX, x), Math.Min(minY, y), Math.Max(maxY, y));
            }
        }
        if (maxX < 0 || maxX >= x1 - 1)
            return false; // nothing there, or text running on (a base line like "2,625 Armor" has no icon)
        int width = maxX - minX + 1, height = maxY - minY + 1;
        return width >= w * MinStar && height >= w * MinStar && width <= w * MaxStar && height <= w * MaxStar
            && Math.Abs(width - height) <= Math.Max(2, w / 170);
    }
}
