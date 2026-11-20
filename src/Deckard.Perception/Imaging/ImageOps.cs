using Deckard.Core.Capture;

namespace Deckard.Perception.Imaging;

public static class ImageOps
{
    /// <summary>
    /// Whether a small patch (61x9 px around the point) is a dark game panel: dim and hardly red. The skill tree and
    /// paragon screens' tab bar is (about RGB 16,19,19 at 3440x1440); a red-lit game world around a red tab is not.
    /// </summary>
    public static bool IsDarkPanel(Frame frame, int cx, int cy)
    {
        int n = 0, red = 0;
        long brightest = 0;
        var px = frame.Pixels;
        for (var y = Math.Max(0, cy - 4); y <= Math.Min(frame.Height - 1, cy + 4); y++)
            for (var x = Math.Max(0, cx - 30); x <= Math.Min(frame.Width - 1, cx + 30); x += 2)
            {
                var o = y * frame.Stride + x * Frame.BytesPerPixel;
                int b = px[o], g = px[o + 1], r = px[o + 2];
                brightest += Math.Max(r, Math.Max(g, b));
                n++;
                if (r > 30 && r > 2 * g && r > 2 * b)
                    red++;
            }
        return n > 0 && brightest / n <= 45 && red < n / 10;
    }

    /// <summary>
    /// Whether the frame is black all over, as Windows.Graphics.Capture delivers a game in exclusive fullscreen. Checks
    /// a 64 x 36 grid of pixels: even the darkest game scene has its HUD (globes, skill bar) somewhere on the grid.
    /// </summary>
    /// <summary>Half the size (each 2x2 block averaged), e.g. to fit a 4K screenshot in a bug report. The caller owns the result.</summary>
    public static Frame Halve(Frame source)
    {
        var half = Frame.Create(Math.Max(1, source.Width / 2), Math.Max(1, source.Height / 2), source.Timestamp);
        var from = source.Pixels;
        var to = half.Pixels;
        for (var y = 0; y < half.Height; y++)
        {
            var y0 = Math.Min(2 * y, source.Height - 1) * source.Stride;
            var y1 = Math.Min(2 * y + 1, source.Height - 1) * source.Stride;
            for (var x = 0; x < half.Width; x++)
            {
                var x0 = Math.Min(2 * x, source.Width - 1) * Frame.BytesPerPixel;
                var x1 = Math.Min(2 * x + 1, source.Width - 1) * Frame.BytesPerPixel;
                var o = y * half.Stride + x * Frame.BytesPerPixel;
                for (var c = 0; c < Frame.BytesPerPixel; c++)
                    to[o + c] = (byte)((from[y0 + x0 + c] + from[y0 + x1 + c] + from[y1 + x0 + c] + from[y1 + x1 + c] + 2) / 4);
            }
        }
        return half;
    }

    public static bool IsBlack(Frame frame, byte threshold = 4)
    {
        var pixels = frame.Pixels;
        for (var gy = 0; gy < 36; gy++)
        {
            var y = (int)((gy + 0.5) * frame.Height / 36);
            for (var gx = 0; gx < 64; gx++)
            {
                var i = y * frame.Stride + (int)((gx + 0.5) * frame.Width / 64) * Frame.BytesPerPixel;
                if (pixels[i] > threshold || pixels[i + 1] > threshold || pixels[i + 2] > threshold)
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Prepares tooltip text for OCR: each pixel becomes its brightest channel (so blue, orange and purple text turn
    /// as bright as white text), levels are stretched between <paramref name="black"/> and <paramref name="white"/>,
    /// and the image is optionally upscaled with bilinear filtering. Returns a new frame the caller owns.
    /// </summary>
    public static Frame PrepareForOcr(Frame source, int scale = 1, byte black = 40, byte white = 200)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);
        var width = source.Width * scale;
        var height = source.Height * scale;
        var result = Frame.Create(width, height, source.Timestamp);

        // Brightness per source pixel, level-stretched.
        var levels = new byte[256];
        for (var v = 0; v < 256; v++)
            levels[v] = (byte)Math.Clamp((v - black) * 255 / Math.Max(1, white - black), 0, 255);
        var gray = new byte[source.Width * source.Height];
        var src = source.Pixels;
        for (var i = 0; i < gray.Length; i++)
        {
            var p = i * Frame.BytesPerPixel;
            gray[i] = levels[Math.Max(src[p], Math.Max(src[p + 1], src[p + 2]))];
        }

        var dst = result.Pixels;
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Min((y + 0.5f) / scale - 0.5f, source.Height - 1);
            var y0 = Math.Max(0, (int)sy);
            var y1 = Math.Min(y0 + 1, source.Height - 1);
            var fy = Math.Max(0, sy - y0);
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Min((x + 0.5f) / scale - 0.5f, source.Width - 1);
                var x0 = Math.Max(0, (int)sx);
                var x1 = Math.Min(x0 + 1, source.Width - 1);
                var fx = Math.Max(0, sx - x0);
                var top = gray[y0 * source.Width + x0] * (1 - fx) + gray[y0 * source.Width + x1] * fx;
                var bottom = gray[y1 * source.Width + x0] * (1 - fx) + gray[y1 * source.Width + x1] * fx;
                var value = (byte)(top * (1 - fy) + bottom * fy + 0.5f);
                var o = (y * width + x) * Frame.BytesPerPixel;
                dst[o] = dst[o + 1] = dst[o + 2] = value;
                dst[o + 3] = 255;
            }
        }
        return result;
    }
}
