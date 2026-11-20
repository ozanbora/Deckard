using System.Globalization;
using System.Text.RegularExpressions;
using Deckard.Core.Items;

namespace Deckard.Perception.Tooltips;

/// <summary>A read value that looks misread: which of the line's values, and the range it should be in.</summary>
/// <param name="Shown">The range is the tooltip's own ("[206 - 248]"), not the season data's.</param>
public sealed record ValueFlag(int Value, double Low, double High, bool Shown);

/// <summary>
/// Checks the values read off a tooltip line. Windows' OCR gives no confidence, so a value is suspect when it falls
/// outside the range the tooltip prints after it (the advanced view's "[206 - 248]"), or, without one, far outside the
/// season data's roll range for the item's power.
/// </summary>
public static partial class ReadCheck
{
    public static IReadOnlyList<ValueFlag> Check(ItemTooltip item, TooltipLine line, AffixCatalog catalog)
    {
        // What the player typed or confirmed stands: their word against the reader's.
        if (line.Matches is not [var match, ..] || match.Values.Count == 0 || line.Confirmed || line.ReadValues is not null)
            return [];
        var flags = new List<ValueFlag>();
        var values = match.Values;

        // The tooltip's own ranges, one per value, in order. A "]" read as a 1 only widens the range ("[4.0 - 6.01"); a
        // range that wrapped ("[10 -" / "20]") lost its end with the letterless line, and bounds the value from below.
        var shown = ShownRange().Matches(line.Text)
            .Select(m => (Low: Number(m.Groups[1].Value), High: m.Groups[2].Success ? Number(m.Groups[2].Value) : double.PositiveInfinity)).ToList();
        var hasShown = shown.Count == values.Count && shown.All(r => r.Low <= r.High);
        if (hasShown)
        {
            for (var i = 0; i < values.Count; i++)
                if (values[i] < shown[i].Low || values[i] > shown[i].High)
                    flags.Add(new ValueFlag(i, shown[i].Low, shown[i].High, Shown: true));
            return flags;
        }

        // The season's range. Unique and mythic powers don't follow the base formulas.
        var text = match.Entry.Ids.Select(id => catalog.AffixText.GetValueOrDefault(id))
            .FirstOrDefault(t => t is not null && match.LineIndex < t.Lines.Count && t.Lines[match.LineIndex] == match.Entry.Lines[match.LineIndex]);
        if (text is null || text.MagicType is 2 or 4 || item.ItemPower is not { } power)
            return flags;
        var scale = catalog.Items.Values.Any(i => i.TwoHanded && string.Equals(i.Type, item.ItemType, StringComparison.OrdinalIgnoreCase)) ? 2 : 1;
        var slot = text.Lines.Take(match.LineIndex).Sum(l => l.Count(c => c == '#'));
        for (var i = 0; i < values.Count; i++, slot++)
        {
            // Below the data's first item power tier there's no range to hold a value to.
            if (slot >= text.Slots.Count || text.Slots[slot] is not [var index, var factor, _] || text.Ranges?.ElementAtOrDefault((int)index) is not [[var firstTier, ..], ..]
                || power < firstTier || text.RangeAt((int)index, power) is not [var a, var b])
                continue;
            var (low, high) = (Math.Min(a * factor, b * factor) * scale, Math.Max(a * factor, b * factor) * scale);
            // shortcut: only a lost or extra digit is caught here (greater affixes, masterworking and slot bonuses raise a
            // value above its base range, by how much the data doesn't say), tighten once the data has those factors.
            if (values[i] < low * 0.5 || values[i] > high * 6)
                flags.Add(new ValueFlag(i, low, high, Shown: false));
        }
        return flags;
    }

    private static double Number(string text) => double.Parse(text.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\[\s*(\d[\d,]*(?:\.\d+)?)\s*-\s*(\d[\d,]*(?:\.\d+)?)?")]
    private static partial Regex ShownRange();
}
