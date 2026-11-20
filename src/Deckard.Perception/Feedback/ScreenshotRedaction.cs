using System.Text.RegularExpressions;
using Deckard.Core.Capture;
using Deckard.Perception.Ocr;

namespace Deckard.Perception.Feedback;

/// <summary>
/// Covers personal data in a bug report's screenshot before it leaves the player's PC: suggests boxes (the player
/// reviews them, adds their own and removes any) and burns them in as solid black, which, unlike blur, can't be undone.
/// </summary>
public static partial class ScreenshotRedaction
{
    // A BattleTag as OCR reads it: a name, '#', digits ("Demo8484x2#2002", "TempestBard#2193").
    [GeneratedRegex(@"\p{L}[\p{L}\p{N}]{1,15}\s?#\s?\d{3,6}")]
    private static partial Regex BattleTag();

    // Deckard's deals name the partner's zone after "in game": "… · in game · in Gea Kul".
    [GeneratedRegex(@"in game\s*[·•.]\s*in\s+\p{L}")]
    private static partial Regex SharedZone();

    /// <summary>
    /// Boxes to suggest: every OCR line with a BattleTag or a shared zone; in a game screenshot also the chat box and the
    /// party frames, where other players' names show.
    /// </summary>
    /// <param name="game">A Diablo IV screenshot (else one of Deckard's own windows).</param>
    public static IReadOnlyList<PixelRect> Suggest(IReadOnlyList<TextLine> lines, int width, int height, bool game)
    {
        var bounds = new PixelRect(0, 0, width, height);
        var boxes = lines.Where(l => BattleTag().IsMatch(l.Text) || SharedZone().IsMatch(l.Text))
            .Select(l => Grow(l.Bounds, Math.Max(2, l.Bounds.Height / 4)).Intersect(bounds))
            .ToList();
        if (game)
        {
            // The game lays out its HUD by screen height from the left edge. shortcut: estimated from the 16:9 layout,
            // not calibrated on captures with chat and a party; calibrate once there are some (the player reviews anyway).
            var h = height;
            boxes.Add(new PixelRect(0, (int)(0.62 * h), (int)(0.55 * h), (int)(0.27 * h)).Intersect(bounds)); // chat
            boxes.Add(new PixelRect(0, (int)(0.18 * h), (int)(0.20 * h), (int)(0.38 * h)).Intersect(bounds)); // party frames
        }
        return boxes.Where(b => b.Area > 0).ToList();
    }

    /// <summary>Fills each box with solid black, in place.</summary>
    public static void Fill(Frame frame, IEnumerable<PixelRect> boxes)
    {
        var pixels = frame.Pixels;
        foreach (var box in boxes)
        {
            var b = box.Intersect(new PixelRect(0, 0, frame.Width, frame.Height));
            for (var y = b.Y; y < b.Y + b.Height; y++)
            {
                var row = pixels.Slice(y * frame.Stride + b.X * Frame.BytesPerPixel, b.Width * Frame.BytesPerPixel);
                row.Clear();
                for (var x = 3; x < row.Length; x += Frame.BytesPerPixel)
                    row[x] = 255; // opaque
            }
        }
    }

    private static PixelRect Grow(PixelRect r, int by) => new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);
}
