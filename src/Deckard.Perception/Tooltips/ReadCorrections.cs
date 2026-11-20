using System.Globalization;

namespace Deckard.Perception.Tooltips;

/// <summary>What the player corrected on one item: the item power, and values by <see cref="ReadCorrections.LineKey"/>.</summary>
public sealed record ItemCorrection(int? ItemPower, IReadOnlyDictionary<string, double[]> Lines, DateTimeOffset At);

/// <summary>
/// The player's corrections of misread values. They're kept by what was read (the item's name, type and item power, and
/// each line's affix with the values read), so the same misread gets its correction again, and another roll of the item,
/// whose values read differently, never does.
/// </summary>
public static class ReadCorrections
{
    /// <summary>How many items' corrections an account keeps, the oldest going first.</summary>
    public const int Kept = 500;

    public static string ItemKey(ItemTooltip item) => $"{item.Name}|{item.ItemType}|{(item.ItemPowerEdited ? item.ReadItemPower : item.ItemPower)}";

    /// <summary>The line's affix and its values as read; "" for a line no template knows, which has no values to correct.</summary>
    public static string LineKey(TooltipLine line) => line.Matches is [var match, ..]
        ? $"{match.Entry.Keys[0]}#{match.LineIndex}:{string.Join(",", (line.ReadValues ?? match.Values).Select(v => v.ToString(CultureInfo.InvariantCulture)))}"
        : "";

    /// <summary>The item with a line's values replaced; values equal to the read ones undo the correction.</summary>
    public static ItemTooltip WithValues(ItemTooltip item, int line, IReadOnlyList<double> values)
    {
        var old = item.Lines[line];
        if (old.Matches is not [var first, ..] || first.Values.Count != values.Count)
            return item;
        var read = old.ReadValues ?? first.Values;
        var corrected = old with
        {
            Matches = [.. old.Matches.Select(m => m.Values.Count == values.Count ? m with { Values = [.. values] } : m)],
            ReadValues = read.SequenceEqual(values) ? null : read,
            Confirmed = false,
        };
        return item with { Lines = [.. item.Lines.Select((l, i) => i == line ? corrected : l)] };
    }

    /// <summary>
    /// What the player entered for a line: other values correct it; the values it shows confirm them, so a value flagged
    /// as misread that is right stops being flagged.
    /// </summary>
    public static ItemTooltip Entered(ItemTooltip item, int line, IReadOnlyList<double> values) =>
        item.Lines[line].Matches is [var shown, ..] && shown.Values.SequenceEqual(values)
            ? item with { Lines = [.. item.Lines.Select((l, i) => i == line ? l with { Confirmed = true } : l)] }
            : WithValues(item, line, values);

    public static ItemTooltip Reverted(ItemTooltip item, int line) =>
        item.Lines[line].ReadValues is { } read ? WithValues(item, line, read) : item;

    /// <summary>The item with its item power replaced; the read power undoes the correction.</summary>
    public static ItemTooltip WithItemPower(ItemTooltip item, int? power)
    {
        var read = item.ItemPowerEdited ? item.ReadItemPower : item.ItemPower;
        return item with { ItemPower = power, ItemPowerEdited = power != read, ReadItemPower = power != read ? read : null };
    }

    public static bool IsEdited(ItemTooltip item) => item.ItemPowerEdited || item.Lines.Any(l => l.ReadValues is not null);

    private static bool Touched(TooltipLine line) => line.ReadValues is not null || line.Confirmed;

    /// <summary>What to keep of an item's corrections and confirmed values (kept as the values read); null when it has none (left).</summary>
    public static ItemCorrection? Of(ItemTooltip item, DateTimeOffset at) => item.ItemPowerEdited || item.Lines.Any(Touched)
        ? new ItemCorrection(item.ItemPowerEdited ? item.ItemPower : null,
            item.Lines.Where(Touched).GroupBy(LineKey).ToDictionary(g => g.Key, g => g.First().Matches[0].Values.ToArray()), at)
        : null;

    /// <summary>A fresh read with the corrections kept for it.</summary>
    public static ItemTooltip Apply(ItemTooltip read, IReadOnlyDictionary<string, ItemCorrection>? corrections)
    {
        if (corrections is null || !corrections.TryGetValue(ItemKey(read), out var correction))
            return read;
        var item = correction.ItemPower is { } power ? WithItemPower(read, power) : read;
        for (var i = 0; i < item.Lines.Count; i++)
            if (correction.Lines.TryGetValue(LineKey(item.Lines[i]), out var values))
                item = Entered(item, i, values); // the read values themselves: confirmed
        return item;
    }

    /// <summary>A value as the player typed it: "1,770", "10.5", "+10,5%" (a decimal comma has one or two digits after it); null when it's no number.</summary>
    public static double? ParseValue(string text)
    {
        text = text.Trim().TrimStart('+', 'x').TrimEnd('%').Replace(" ", "");
        if (text.LastIndexOf(',') is var comma and >= 0 && text.Length - comma - 1 is 1 or 2 && !text.Contains('.'))
            text = text[..comma] + "." + text[(comma + 1)..];
        return double.TryParse(text.Replace(",", ""), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>The account's corrections after one item's changed (null: it has none left), within <see cref="Kept"/>.</summary>
    public static Dictionary<string, ItemCorrection> With(IReadOnlyDictionary<string, ItemCorrection>? corrections, string itemKey, ItemCorrection? correction)
    {
        var all = new Dictionary<string, ItemCorrection>(corrections ?? new Dictionary<string, ItemCorrection>());
        all.Remove(itemKey);
        if (correction is not null)
            all[itemKey] = correction;
        foreach (var old in all.OrderByDescending(c => c.Value.At).Skip(Kept).Select(c => c.Key).ToList())
            all.Remove(old);
        return all;
    }
}
