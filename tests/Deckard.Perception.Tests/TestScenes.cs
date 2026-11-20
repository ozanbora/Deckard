using Deckard.Core.Capture;

namespace Deckard.Perception.Tests;

/// <summary>Synthetic game frames with tooltip-like panels, colors measured on Season 15 captures.</summary>
internal static class TestScenes
{
    public static readonly (byte R, byte G, byte B) Stone = (90, 92, 85);   // game world
    public static readonly (byte R, byte G, byte B) Body = (28, 21, 13);    // tooltip background
    public static readonly (byte R, byte G, byte B) Bronze = (112, 97, 59); // tooltip border
    public static readonly (byte R, byte G, byte B) Text = (220, 220, 210);

    /// <summary>A frame of stone with the given tooltip panels, each drawn with an outer and inner border line.</summary>
    public static Frame Scene(int width, int height, params PixelRect[] tooltips)
    {
        var frame = Frame.Create(width, height, TimeSpan.Zero);
        Fill(frame, new PixelRect(0, 0, width, height), Stone);
        foreach (var t in tooltips)
        {
            Fill(frame, t, Body);
            foreach (var x in new[] { t.X, t.X + 4, t.Right - 5, t.Right - 1 })
                Fill(frame, new PixelRect(x, t.Y, 2, t.Height), Bronze);
        }
        return frame;
    }

    public static void Fill(Frame frame, PixelRect rect, (byte R, byte G, byte B) color)
    {
        var pixels = frame.Pixels;
        for (var y = rect.Y; y < rect.Bottom; y++)
            for (var x = rect.X; x < rect.Right; x++)
            {
                var i = y * frame.Stride + x * Frame.BytesPerPixel;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (color.B, color.G, color.R, 255);
            }
    }

    public static void Disc(Frame frame, double cx, double cy, double radius, (byte R, byte G, byte B) color)
    {
        for (var y = (int)(cy - radius); y <= cy + radius; y++)
            for (var x = (int)(cx - radius); x <= cx + radius; x++)
                if (x >= 0 && y >= 0 && x < frame.Width && y < frame.Height && (x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius)
                    Fill(frame, new PixelRect(x, y, 1, 1), color);
    }

    public static void ThickLine(Frame frame, (double X, double Y) a, (double X, double Y) b, double width, (byte R, byte G, byte B) color)
    {
        var steps = (int)Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        for (var i = 0; i <= steps; i++)
            Disc(frame, a.X + (b.X - a.X) * i / steps, a.Y + (b.Y - a.Y) * i / steps, width / 2, color);
    }

    /// <summary>Paints text-like bars inside a tooltip so two items look different.</summary>
    public static void Lines(Frame frame, PixelRect tooltip, int count, int seed, (byte R, byte G, byte B)? color = null)
    {
        var random = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var width = random.Next(tooltip.Width / 4, tooltip.Width * 3 / 4);
            Fill(frame, new PixelRect(tooltip.X + 30, tooltip.Y + 60 + i * 34, width, 14), color ?? Text);
        }
    }
}
