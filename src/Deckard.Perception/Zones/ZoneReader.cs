using System.Text.RegularExpressions;
using Deckard.Core.Capture;
using Deckard.Perception.Imaging;
using Deckard.Perception.Ocr;

namespace Deckard.Perception.Zones;

/// <summary>
/// Reads the zone name Diablo IV shows above the minimap (e.g. "Backwater", next to the TAB key hint and the clock),
/// from pixels only. Used for location sharing with accepted trade partners.
/// </summary>
public static partial class ZoneReader
{
    /// <summary>
    /// The strip above the minimap at the top-right. The HUD scales with the window's height, so the strip is measured
    /// in heights from the right edge: it fits 16:9 and ultrawide alike.
    /// </summary>
    public static PixelRect Region(int width, int height)
    {
        var w = Math.Min(width, (int)(height * 0.5));
        return new PixelRect(width - w, 0, w, Math.Max(1, (int)(height * 0.035)));
    }

    /// <summary>The zone name on this frame, or null when none can be read (menus, map, loading screens).</summary>
    public static async Task<string?> ReadAsync(Frame frame, TextRecognizer ocr)
    {
        using var crop = frame.Crop(Region(frame.Width, frame.Height));
        using var prepared = ImageOps.PrepareForOcr(crop, scale: 2);
        var lines = await ocr.RecognizeAsync(prepared);
        return Parse(string.Join(" ", lines.Select(l => l.Text)));
    }

    /// <summary>
    /// The zone name from the OCR'd strip: drops the clock ("9:25 AM") and the TAB key hint, then keeps the longest run
    /// of words; null when nothing like a name is left.
    /// </summary>
    public static string? Parse(string text)
    {
        var cleaned = Clock().Replace(text, " ");
        cleaned = TabHint().Replace(cleaned, " ");
        var best = NameRun().Matches(cleaned).Select(m => Spaces().Replace(m.Value.Trim(), " ")).OrderByDescending(n => n.Length).FirstOrDefault();
        return best is { Length: >= 3 and <= 40 } && best.Any(char.IsUpper) ? best : null;
    }

    [GeneratedRegex(@"\b\d{1,2}\s*[:.]\s*\d{2}\s*(?:[AP]\.?\s*M\.?)?", RegexOptions.IgnoreCase)]
    private static partial Regex Clock();

    [GeneratedRegex(@"\bTAB\b", RegexOptions.IgnoreCase)]
    private static partial Regex TabHint();

    // Words of letters (with ' and -), separated by single spaces: "Backwater", "Kyovashad", "Ked Bardu", "Zarbinzet".
    [GeneratedRegex(@"[A-Za-z][A-Za-z'\-]*(?:\s[A-Za-z][A-Za-z'\-]*)*")]
    private static partial Regex NameRun();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
