using Deckard.Core.Capture;
using Deckard.Core.Trade;
using Deckard.Perception.Imaging;
using Deckard.Perception.Ocr;

namespace Deckard.Perception.Realms;

/// <summary>
/// Reads the realm of the character on Diablo IV's character select screen: the line under its name, "Seasonal | Lord of
/// Hatred", "Eternal" or "Hardcore". Pixels only, like <see cref="Zones.ZoneReader"/>. The trade realm a player lists in
/// should be the one their character plays in: an Eternal listing is out of a Seasonal character's reach.
/// </summary>
public static class RealmReader
{
    /// <summary>
    /// The band under the character's name, centred (the screen is laid out around its middle on 16:9 and ultrawide alike):
    /// measured at 3440x1440, the line sits at 0.826 of the height. The character list on the left, which names every
    /// character's realm, stays out.
    /// </summary>
    public static PixelRect Region(int width, int height)
    {
        var w = Math.Min(width, (int)(height * 0.6));
        return new PixelRect((width - w) / 2, (int)(height * 0.80), w, Math.Max(1, (int)(height * 0.05)));
    }

    /// <summary>The selected character's realm, or null off the character select screen.</summary>
    public static async Task<Realm?> ReadAsync(Frame frame, TextRecognizer ocr, int season)
    {
        using var crop = frame.Crop(Region(frame.Width, frame.Height));
        using var prepared = ImageOps.PrepareForOcr(crop, scale: 2);
        var lines = await ocr.RecognizeAsync(prepared);
        return Parse(string.Join(" ", lines.Select(l => l.Text)), season);
    }

    /// <summary>
    /// The realm from the OCR'd line: "Seasonal" (or "Season") for the seasonal realm, else "Eternal"; "Hardcore" for
    /// hardcore, which an eternal hardcore character shows alone. Null when none of those words is there.
    /// </summary>
    public static Realm? Parse(string text, int season)
    {
        var seasonal = text.Contains("Season", StringComparison.OrdinalIgnoreCase);
        var eternal = text.Contains("Eternal", StringComparison.OrdinalIgnoreCase);
        var hardcore = text.Contains("Hardcore", StringComparison.OrdinalIgnoreCase);
        if (seasonal == eternal && !(hardcore && !seasonal))
            return null; // neither word, or both (not one character's line)
        return seasonal ? Realm.Seasonal(season, hardcore) : Realm.Eternal(hardcore);
    }
}
