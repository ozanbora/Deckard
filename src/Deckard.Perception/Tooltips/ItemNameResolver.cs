using System.Text;
using Deckard.Core.Items;

namespace Deckard.Perception.Tooltips;

/// <param name="Name">The corrected item name, e.g. "Virtuous Axolotl's Smile".</param>
/// <param name="Confidence">0..1 similarity between the OCR'd name and <paramref name="Name"/>.</param>
/// <param name="BaseName">The base item part of a legendary name, e.g. "Axolotl's Smile".</param>
/// <param name="Power">The aspect a legendary name is built from, or the unique/mythic power of a unique item.</param>
public sealed record ResolvedName(string Name, double Confidence, string? BaseName, AffixEntry? Power);

/// <summary>
/// Corrects OCR'd item names against the season's names. The header font is ornate and OCR mangles it
/// ("Indotiiitable", "Sm1le"), but names are built from a known vocabulary: legendaries are
/// "&lt;aspect prefix&gt; &lt;base&gt;" or "&lt;base&gt; &lt;aspect suffix&gt;", uniques have fixed names, rares pair two listed words.
/// </summary>
public sealed partial class ItemNameResolver
{
    private readonly AffixCatalog _catalog;
    private readonly List<(string Part, bool IsPrefix, AffixEntry Aspect)> _aspectParts;
    private readonly List<AffixEntry> _uniques;

    public ItemNameResolver(AffixCatalog catalog)
    {
        _catalog = catalog;
        _aspectParts = [];
        foreach (var aspect in catalog.Entries.Where(e => e.Kind == AffixKind.Aspect))
        {
            if (aspect.Prefix is { } prefix)
                _aspectParts.Add((Clean(prefix), true, aspect));
            if (aspect.Suffix is { } suffix)
                _aspectParts.Add((Clean(suffix), false, aspect));
        }
        _uniques = [.. catalog.Entries.Where(e => e.Kind is AffixKind.Unique or AffixKind.Mythic && e.Name is not null)];
    }

    /// <summary>Below this similarity the OCR'd name is kept as read.</summary>
    public double MinConfidence { get; init; } = 0.6;

    /// <param name="bodyEntries">Affixes and powers read from the tooltip body. Names are built from them
    /// (magic: affix prefix and suffix; legendary: aspect), so they are tried as name parts.</param>
    public ResolvedName? Resolve(string ocrName, string? rarity, string? itemType, IReadOnlyCollection<AffixEntry>? bodyEntries = null)
    {
        var text = Clean(ocrName);
        if (text.Length < 3)
            return null;

        bodyEntries ??= [];
        ResolvedName? Of(string read) => rarity switch
        {
            "Unique" or "Mythic" => ResolveUnique(read, itemType),
            "Legendary" => ResolveLegendary(read, itemType, bodyEntries),
            "Magic" => ResolveMagic(read, itemType, bodyEntries),
            "Rare" => ResolveRare(read),
            "Common" => null, // a common item is named after its base, which the parser already matched
            _ => Better(ResolveUnique(read, itemType), ResolveLegendary(read, itemType, bodyEntries)),
        };
        // The header font's M reads as "TTI", "NI" or "RN" ("PRITTIE ARBITER" for "PRIME ARBITER"): tried as M too.
        var asM = HeaderM().Replace(text, "m");
        var best = asM == text ? Of(text) : Better(Of(text), Of(asM));
        return best is { } b && b.Confidence >= MinConfidence ? b : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex("tti|ni|rn")]
    private static partial System.Text.RegularExpressions.Regex HeaderM();

    /// <summary>Magic names are "[prefix of one affix] base [suffix of another affix]", either part optional.</summary>
    private ResolvedName? ResolveMagic(string text, string? itemType, IReadOnlyCollection<AffixEntry> bodyEntries)
    {
        if (itemType is null || !_catalog.BaseNames.TryGetValue(itemType, out var bases))
            return null;
        var affixes = bodyEntries.Where(e => e.Kind == AffixKind.Affix).ToList();
        string?[] prefixes = [null, .. affixes.Select(e => e.Prefix).Where(p => p is not null).Distinct()];
        string?[] suffixes = [null, .. affixes.Select(e => e.Suffix).Where(s => s is not null).Distinct()];

        ResolvedName? best = null;
        foreach (var baseName in bases)
            foreach (var prefix in prefixes)
                foreach (var suffix in suffixes)
                {
                    var original = string.Join(" ", new[] { prefix, baseName, suffix }.Where(s => s is not null));
                    var score = Similarity(text, Clean(original));
                    if (best is null || score > best.Confidence)
                        best = new ResolvedName(original, score, baseName, null);
                }
        return best;
    }

    /// <summary>
    /// Rare names are a word pair from the season's lists ("Dire Torment", "Commander's Reaver"): the end of the name
    /// against the second words, what's before it against the first. Whatever OCR read ahead of the name (the game's
    /// world beside the header) falls away. Kept as read unless both words are close.
    /// </summary>
    private ResolvedName? ResolveRare(string text)
    {
        static (string Word, string Clean, double Score)? Closest(IReadOnlyList<string> words, string text) => words.Count == 0 ? null
            : words.Select(w => (Word: w, Clean: Clean(w))).Select(w => (w.Word, w.Clean, Score: Similarity(Tail(text, w.Clean.Length), w.Clean))).MaxBy(w => w.Score);
        if (Closest(_catalog.RareNameSuffixes, text) is not { } second
            || Closest(_catalog.RareNamePrefixes, text[..Math.Max(0, text.Length - second.Clean.Length)].Trim()) is not { } first)
            return null;
        var score = Math.Min(first.Score, second.Score);
        return score >= 0.75 ? new ResolvedName($"{first.Word} {second.Word}", score, null, null) : null;
    }

    private ResolvedName? ResolveUnique(string text, string? itemType)
    {
        ResolvedName? best = null;
        foreach (var unique in _uniques)
        {
            if (itemType is not null && unique.ItemType is not null && !string.Equals(unique.ItemType, itemType, StringComparison.OrdinalIgnoreCase))
                continue;
            var score = Similarity(text, Clean(unique.Name!));
            if (best is null || score > best.Confidence)
                best = new ResolvedName(unique.Name!, score, null, unique);
        }
        return best;
    }

    private ResolvedName? ResolveLegendary(string text, string? itemType, IReadOnlyCollection<AffixEntry> bodyEntries)
    {
        if (itemType is null || !_catalog.BaseNames.TryGetValue(itemType, out var bases) || bases.Count == 0)
            return null;

        // Step 1: which aspect part fits the start (prefix) or end (suffix) of the name best, plus the parts of any
        // aspect read in the body: the name is built from that aspect, so it competes even when the name OCR is poor.
        var parts = _aspectParts
            .Select(p => (p.Part, p.IsPrefix, p.Aspect,
                Score: Similarity(p.IsPrefix ? Head(text, p.Part.Length) : Tail(text, p.Part.Length), p.Part)))
            .OrderByDescending(p => p.Score)
            .Take(3)
            .Concat(_aspectParts.Where(p => bodyEntries.Contains(p.Aspect)).Select(p => (p.Part, p.IsPrefix, p.Aspect, Score: 1.0)))
            .Distinct();

        // Step 2: the rest of the name against the slot's base names; score the assembled name as a whole.
        ResolvedName? best = null;
        foreach (var (part, isPrefix, aspect, _) in parts)
        {
            var rest = isPrefix ? text[Math.Min(text.Length, part.Length)..] : text[..Math.Max(0, text.Length - part.Length)];
            foreach (var baseName in bases.OrderByDescending(b => Similarity(rest.Trim(), Clean(b))).Take(5))
            {
                var original = isPrefix ? $"{aspect.Prefix} {baseName}" : $"{baseName} {aspect.Suffix}";
                var score = Similarity(text, Clean(original));
                if (best is null || score > best.Confidence)
                    best = new ResolvedName(original, score, baseName, aspect);
            }
        }
        return best;
    }

    private static ResolvedName? Better(ResolvedName? a, ResolvedName? b) =>
        a is null ? b : b is null ? a : a.Confidence >= b.Confidence ? a : b;

    private static string Head(string text, int length) => text[..Math.Min(text.Length, length)];

    private static string Tail(string text, int length) => text[Math.Max(0, text.Length - length)..];

    /// <summary>Lowercase letters, apostrophes and single spaces; digits OCR'd for look-alike letters are mapped back.</summary>
    internal static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text.ToLowerInvariant())
        {
            var c = raw switch { '1' or '|' => 'l', '0' => 'o', '5' => 's', '’' or '`' => '\'', '-' => ' ', _ => raw };
            if (char.IsLetter(c) || c == '\'')
                sb.Append(c);
            else if (c == ' ' && sb.Length > 0 && sb[^1] != ' ')
                sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    /// <summary>1 - normalized Levenshtein distance.</summary>
    internal static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
            return 0;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return 1.0 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }
}
