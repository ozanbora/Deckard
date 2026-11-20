using System.Text.RegularExpressions;
using Deckard.Core.Builds;
using Deckard.Core.Items;
using Deckard.Perception.Tooltips;

namespace Deckard.Perception.Builds;

/// <summary>
/// Runewords for an item's sockets: a Rune of Ritual (gains Offering when its condition happens) and a Rune of Invocation
/// (spends it on its effect) in one item with two sockets; only two runewords are active at a time. Suggested from the
/// build guide first, then from the runes the player owns (learnt from their tooltips, "TAL (4)").
/// </summary>
public static partial class Runewords
{
    /// <summary>A rune's tooltip: the rune by its season key and how many the player has ("TAL (4)"; no count is one).</summary>
    public static (string Key, int Count)? Owned(ItemTooltip tooltip, AffixCatalog catalog) =>
        Identify(tooltip, catalog) is (GoodsInfo rune, int count) ? (rune.Key, count) : null;

    /// <summary>Whether a tooltip is a rune's, by its type line ("Unique Rune of Ritual", "Magic Rune of Invocation").</summary>
    public static bool IsRune(ItemTooltip tooltip) => tooltip.ItemType?.StartsWith("Rune of", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The rune a tooltip shows and how many the player has, or (null, count) when it can't be told. The name alone isn't
    /// enough: the game draws O as Ø (OCR reads "Crt" for ØRT, or another rune's name) and the tooltip's top can take in
    /// text above it ("4,809 Tal (4)"). So each rune of the tooltip's side (Ritual or Invocation) is scored on its effect
    /// text, its Offering ("Gain: 1 Offering", "Requires: 25 Offering") and its name, and the one clearly best is taken.
    /// </summary>
    public static (GoodsInfo? Rune, int Count) Identify(ItemTooltip tooltip, AffixCatalog catalog)
    {
        var header = tooltip.RawName ?? tooltip.Name ?? "";
        var count = Count().Matches(header) is { Count: > 0 } counts ? int.Parse(counts[^1].Groups[1].Value) : 1;
        if (!IsRune(tooltip))
            return (null, count);
        var ritual = tooltip.ItemType!.Contains("Ritual", StringComparison.OrdinalIgnoreCase);
        var body = Words(string.Join(" ", tooltip.Lines.Select(l => l.Text)));
        var offering = OfferingLine().Match(string.Join(" ", tooltip.Lines.Select(l => l.Text))) is { Success: true } o ? int.Parse(o.Groups[1].Value) : (int?)null;
        // The name: the word just before "(N)", or the header's last word.
        var named = NameWord().Match(header) is { Success: true } w ? Letters(w.Groups[1].Value) : "";
        var scored = catalog.Goods.Where(g => g.Ritual == ritual)
            .Select(g => (Rune: g, Score:
                (g.Lines is [var effect, ..] && Words(effect) is { Length: > 0 } e && body.Contains(e, StringComparison.Ordinal) ? 3 : 0)
                + (offering == g.Offering ? 2 : 0)
                + (Letters(g.Name) == named ? 2 : named.Length > 0 && Distance(Letters(g.Name), named) == 1 ? 1 : 0)))
            .OrderByDescending(s => s.Score).ToList();
        // Its name alone will do (2), or its effect, or its Offering when no other rune of the side shares it; a near
        // name alone won't. A misread that's another rune's name ("Eth" for ØRT) loses to the effect and Offering read.
        return scored is [var best, ..] && best.Score >= 2 && (scored.Count == 1 || scored[1].Score < best.Score) ? (best.Rune, count) : (null, count);
    }

    /// <summary>Lower-case words and numbers, single-spaced: how effect text is compared.</summary>
    private static string Words(string text) => string.Join(" ", WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value));

    /// <summary>Edits between two short names (Levenshtein).</summary>
    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var (diagonal, row0) = (row[0], i);
            row[0] = row0;
            for (var j = 1; j <= b.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }

    /// <summary>
    /// What the card says about an item's sockets: what the guide sockets in its slot, then a runeword to make, the guide's
    /// when the player has both runes, else the player's own pairs by how few Ritual triggers pay for one Invocation.
    /// <paramref name="owned"/> is null until a rune tooltip has been read.
    /// </summary>
    public static IReadOnlyList<string> Advise(ItemTooltip item, PlannedItem? planned, BuildVariant? plan,
        IReadOnlyDictionary<string, int>? owned, AffixCatalog catalog)
    {
        var lines = new List<string>(Socketed(item, planned, catalog));
        if (item.EmptySockets == 0)
            return lines;
        string Name(string key) => catalog.Goods.FirstOrDefault(g => g.Key == key)?.Name ?? catalog.Socketables.GetValueOrDefault(key)?.Name ?? key;
        bool Has(string key) => owned?.GetValueOrDefault(key) > 0;

        var guideHere = planned?.Sockets?.Where(k => k.Length > 0).ToList() ?? [];
        var pairHere = Pair(guideHere, catalog);
        // An item of another type in the guide's place (an axe where it has a two-handed sword): said with the guide's item.
        var where = planned is not null && !string.Equals(catalog.Items.GetValueOrDefault(planned.ItemKey)?.Type, item.ItemType, StringComparison.OrdinalIgnoreCase)
            ? $"in its {Slot(planned, catalog)}" : "here";
        if (pairHere is (string r, string i))
            lines.Add($"The guide's runeword {where}: {Name(r)} + {Name(i)}"
                + (owned is null ? "." : Has(r) && Has(i) ? ". You have both." : $". You have no {string.Join(" or ", new[] { r, i }.Where(k => !Has(k)).Select(Name))}."));
        else if (guideHere.Count > 0)
            // What to put in this item's empty sockets (the player is taken to have the guide's gems), as many as it has.
            lines.Add("Socket " + string.Join(", ", guideHere.Take(item.EmptySockets).GroupBy(Name).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key))
                + (where == "here" ? " here." : $" here, as the guide does {where}."));
        if (item.EmptySockets < 2 || pairHere is not null && owned is not null && Has(pairHere.Value.Ritual) && Has(pairHere.Value.Invocation))
            return lines; // a runeword takes two sockets; or the guide's own is ready to go in

        // The guide's runewords in its other items, which the player can make.
        var guidePairs = (plan?.Items.Values ?? []).Where(p => p != planned)
            .Select(p => (Item: p, Pair: Pair(p.Sockets ?? [], catalog))).Where(p => p.Pair is not null).ToList();
        if (owned is null)
        {
            if (guidePairs.Count > 0)
                lines.Add("The guide's runewords: " + string.Join(", ", guidePairs.Select(p => $"{Name(p.Pair!.Value.Ritual)} + {Name(p.Pair!.Value.Invocation)} in its {Slot(p.Item, catalog)}")) + ".");
            lines.Add("Hover your runes once (Character, Socketables) and Deckard suggests a runeword from them.");
            return lines;
        }
        // The runes saved, Rituals then Invocations.
        string Listed(bool ritual) => string.Join(", ", catalog.Goods.Where(g => g.Ritual == ritual && Has(g.Key))
            .OrderBy(g => g.Name, StringComparer.Ordinal).Select(g => owned![g.Key] > 1 ? $"{g.Name} ×{owned[g.Key]}" : g.Name));
        lines.Add($"Your runes: Ritual {(Listed(true) is { Length: > 0 } r1 ? r1 : "none")}; Invocation {(Listed(false) is { Length: > 0 } i1 ? i1 : "none")}.");
        var canMake = guidePairs.Where(p => Has(p.Pair!.Value.Ritual) && Has(p.Pair!.Value.Invocation)).ToList();
        // Said with where the guide puts it, so it isn't taken for this item's (the guide may want gems here).
        foreach (var (guideItem, pair) in canMake)
            lines.Add($"You have both runes for the guide's {Slot(guideItem, catalog)} runeword: {Name(pair!.Value.Ritual)} + {Name(pair!.Value.Invocation)}"
                + (guideHere.Count > 0 ? $", for your {Slot(guideItem, catalog)}, not this item." : $". With no runeword here in the guide, this item can hold it."));
        if (canMake.Count > 0)
        {
            lines.Add("Only two runewords are active at a time.");
            return lines;
        }

        // The player's own runes: each Ritual with each Invocation, fewest Ritual triggers per cast first.
        var runes = catalog.Goods.Where(g => g.Ritual is not null && Has(g.Key)).ToList();
        var own = runes.Where(g => g.Ritual == true)
            .SelectMany(ritual => runes.Where(g => g.Ritual == false).Select(invocation => (Ritual: ritual, Invocation: invocation,
                Triggers: (int)Math.Ceiling(invocation.Offering / (double)Math.Max(1, ritual.Offering)))))
            .OrderBy(p => p.Triggers).ThenBy(p => p.Ritual.Name, StringComparer.Ordinal).Take(3).ToList();
        if (own.Count == 0)
        {
            lines.Add("No runeword from your runes: it takes a Rune of Ritual and a Rune of Invocation.");
            return lines;
        }
        lines.Add("Runewords from your runes, by how often they cast:");
        foreach (var (ritual, invocation, triggers) in own)
            lines.Add($"{ritual.Name} + {invocation.Name}: {Effect(ritual)} gains {ritual.Offering}, {invocation.Name} needs {invocation.Offering}"
                + (triggers <= 1 ? ", a cast each time." : $", a cast every {triggers} times.") + $" {Effect(invocation)}");
        lines.Add("Pick one whose condition your build meets often. Only two runewords are active at a time.");
        return lines;
    }

    /// <summary>The runes socketed in an item, by key: each runeword's two, named at the start of its line ("CemQua (75/50) - ...").</summary>
    public static IReadOnlyList<string> SocketedRunes(ItemTooltip item, AffixCatalog catalog) =>
        [.. item.Socketed.Select(l => RunePair().Match(l)).Where(m => m.Success)
            .SelectMany(m => new[] { m.Groups[1].Value, m.Groups[2].Value })
            .Select(name => catalog.Goods.FirstOrDefault(g => g.Ritual is not null && Letters(g.Name) == Letters(name))?.Key).OfType<string>()];

    /// <summary>
    /// What an item has socketed, for its card: each runeword by its runes (marked when it's the guide's for the slot),
    /// then the gems by name and count, with the effect they give here.
    /// </summary>
    public static IReadOnlyList<string> Socketed(ItemTooltip item, PlannedItem? planned, AffixCatalog catalog)
    {
        var lines = new List<string>();
        var guide = Pair(planned?.Sockets ?? [], catalog);
        foreach (var line in item.Socketed)
        {
            if (RunePair().Match(line) is not { Success: true } m)
                continue;
            var runes = new[] { m.Groups[1].Value, m.Groups[2].Value }
                .Select(n => catalog.Goods.FirstOrDefault(g => g.Ritual is not null && Letters(g.Name) == Letters(n))).ToList();
            var named = string.Join(" + ", runes.Select((r, i) => r?.Name ?? m.Groups[i + 1].Value));
            var isGuide = guide is (string r1, string i1) && runes.Select(r => r?.Key).OrderBy(k => k).SequenceEqual(new[] { r1, i1 }.OrderBy(k => k));
            lines.Add($"Runeword socketed: {named}" + (isGuide ? ", the guide's ✓" : planned is null ? "" : " (not the guide's for this slot)"));
        }
        // A gem's line is its effect here ("+20 Willpower"): named by the gem whose effect it is.
        string Plain(string t) => Words(t);
        var gems = item.Socketed.Where(l => !RunePair().IsMatch(l))
            .Select(l => (Text: LeadingIcon().Replace(l, ""), Gem: catalog.Goods.FirstOrDefault(g => g.Kind == "Gem" && (g.Lines ?? []).Any(e => Plain(SlotLabel().Replace(e, "")) == Plain(l)))?.Name
                ?? catalog.Socketables.Values.FirstOrDefault(s => (s.Lines ?? []).Any(e => Plain(SlotLabel().Replace(e, "")) == Plain(l)))?.Name))
            .GroupBy(g => (g.Gem, g.Text));
        foreach (var gem in gems)
        {
            var times = gem.Count() > 1 ? $" ×{gem.Count()}" : "";
            lines.Add(gem.Key.Gem is { } gemName ? $"{gemName}{times} socketed: {gem.Key.Text}{(times.Length > 0 ? " each" : "")}" : $"Socketed: {gem.Key.Text}{times}");
        }
        return lines;
    }

    /// <summary>The Ritual and Invocation rune among an item's sockets, or null when it holds no such pair.</summary>
    internal static (string Ritual, string Invocation)? Pair(IEnumerable<string> sockets, AffixCatalog catalog)
    {
        var runes = sockets.Select(k => catalog.Goods.FirstOrDefault(g => g.Key == k)).OfType<GoodsInfo>().ToList();
        return runes.FirstOrDefault(g => g.Ritual == true) is { } ritual && runes.FirstOrDefault(g => g.Ritual == false) is { } invocation
            ? (ritual.Key, invocation.Key) : null;
    }

    /// <summary>Where a planned item goes, by its type ("chest armor (Runic Mail)"), its name alone when its type isn't known.</summary>
    private static string Slot(PlannedItem item, AffixCatalog catalog) =>
        catalog.Items.GetValueOrDefault(item.ItemKey) is { } info ? $"{info.Type.ToLowerInvariant()} ({info.Name})" : item.ItemKey;

    /// <summary>A rune's own text: its condition or effect, the first of its lines ("Deal Damage over Time").</summary>
    private static string Effect(GoodsInfo rune) => rune.Lines is [var first, ..] ? first.TrimEnd('.') : rune.Name;

    /// <summary>A rune's name in plain capitals: the game's font draws O as Ø, which OCR may read as Ø, 0 or @.</summary>
    private static string Letters(string name) => Regex.Replace(name.ToUpperInvariant(), "[Ø0@]", "O").Trim();

    [GeneratedRegex(@"\(\s*(\d{1,3})\s*\)")]
    private static partial Regex Count();

    // A socketed runeword's line starts with its two runes run together ("CemQua", "@NeoCeh" with the icon read as @).
    [GeneratedRegex(@"^\W*([A-ZØ][a-zø]+)([A-ZØ][a-zø]+)")]
    private static partial Regex RunePair();

    [GeneratedRegex(@"^\s*(Weapon|Armor|Jewelry)\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SlotLabel();

    // A socket's icon before its line, as OCR reads it ("@+20 Willpower").
    [GeneratedRegex(@"^[^\w+\-]+")]
    private static partial Regex LeadingIcon();

    [GeneratedRegex(@"([^\s(),]+)\s*(?:\(\s*\d{1,3}\s*\))?\s*$")]
    private static partial Regex NameWord();

    [GeneratedRegex(@"(?:Gain|Requires)\s*:?\s*(\d{1,4})\s*Offering", RegexOptions.IgnoreCase)]
    private static partial Regex OfferingLine();

    [GeneratedRegex(@"[a-z0-9%]+")]
    private static partial Regex WordPattern();
}
