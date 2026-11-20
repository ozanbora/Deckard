using System.Globalization;
using System.Text.RegularExpressions;
using Deckard.Core.Capture;
using Deckard.Core.Items;
using Deckard.Perception.Ocr;

namespace Deckard.Perception.Tooltips;

/// <summary>
/// Turns the OCR lines of one tooltip into an <see cref="ItemTooltip"/>. Tooltip layout, top to bottom:
/// name (one or more lines), type line ("Ancestral Legendary Ring"), item power, quality, then stat and affix lines,
/// then a footer starting at "Requires Level". Affix text itself comes from the season's <see cref="AffixCatalog"/>.
/// </summary>
public sealed partial class TooltipParser(AffixCatalog catalog)
{
    private readonly ItemNameResolver _names = new(catalog);
    private readonly string[] _setNames = [.. catalog.Entries.Where(e => e.Kind == AffixKind.SetBonus && e.Name is not null).Select(e => e.Name!).Distinct()];

    // Gems' and seasonal socketables' effects in a slot, as words ("armor: +20 willpower" -> "20 willpower"): how a
    // socketed gem's line reads in a tooltip.
    private readonly HashSet<string> _gemEffects =
    [
        .. catalog.Goods.Where(g => g.Kind == "Gem").SelectMany(g => g.Lines ?? []).Concat(catalog.Socketables.Values.SelectMany(s => s.Lines ?? []))
            .Select(l => Plain(SlotLabel().Replace(l, ""))).Where(l => l.Length > 0),
    ];

    private bool IsGemEffect(string text) => _gemEffects.Contains(Plain(text));

    /// <summary>Lower-case letters and digits, single-spaced: how a socketed gem's line is compared with the gems' effects.</summary>
    private static string Plain(string text) => string.Join(" ", PlainWords().Matches(text.ToLowerInvariant()).Select(m => m.Value));

    /// <summary>How many OCR lines a wrapped affix or aspect paragraph may span (unique powers run to eight).</summary>
    public int MaxWrappedLines { get; init; } = 8;

    public ItemTooltip Parse(IReadOnlyList<TextLine> ocrLines, PixelRect tooltipBounds)
    {
        // Drop affix icons read as "*" or "@", and letterless pieces of a wrapped range or comparison delta
        // ("[20.0 -", "+8.5%)"); both would split wrapped paragraphs. A bare value like "+2,698" stays, unless the line
        // before left a range open ("... Reduction [4.0 -"): then it's the range's end, its "]" often read as a 1 ("6.01%").
        // An affix the game prints straight after a sentence ("...does not refresh.+11 All Stats") gets a line of its own;
        // a range's tail left alone when OCR lost its start ("+7 Willpower - 9]" for "+[7 - 9]") goes.
        var joined = JoinRows(ocrLines)
            .SelectMany(l => GluedAffix().Split(l.Text).Where(t => t.Length > 0).Select(t => l with { Text = t }))
            .Select(l => l.Text.Contains('[') ? l : l with { Text = OrphanRangeTail().Replace(l.Text, "") })
            .ToList();
        var lines = joined
            .Where((l, i) => l.Text.Any(char.IsLetterOrDigit) && !(l.Text.All(c => !char.IsLetter(c))
                && (l.Text.IndexOfAny(['(', ')', '[', ']']) >= 0 || i > 0 && OpenRange().IsMatch(joined[i - 1].Text))))
            .ToList();

        var typeIndex = lines.FindIndex(l => TypeLine().IsMatch(l.Text));
        string? name = null, typeLine = null, rarity = null, itemType = null;
        var ancestral = false;
        var bodyStart = 0;
        // A common (white) item's type line is the bare type ("Dagger"), with no rarity before it.
        var commonIndex = typeIndex >= 0 ? -1
            : lines.Take(5).ToList().FindIndex(l => catalog.BaseNames.ContainsKey(l.Text.Trim()));
        if (commonIndex > 0)
        {
            name = Name(lines.Take(commonIndex));
            typeLine = lines[commonIndex].Text.Trim();
            bodyStart = commonIndex + 1;
            rarity = "Common";
            itemType = typeLine;
        }
        else if (typeIndex >= 0)
        {
            name = Name(lines.Take(typeIndex));
            typeLine = lines[typeIndex].Text;
            bodyStart = typeIndex + 1;
            // The type line wraps when the slot name is long ("Ancestral Legendary Chest" / "Armor").
            if (bodyStart < lines.Count && IsTypeContinuation(lines[bodyStart].Text))
                typeLine += " " + lines[bodyStart++].Text;
            var m = TypeLine().Match(typeLine);
            ancestral = m.Groups["ancestral"].Success;
            rarity = m.Groups["rarity"].Value.Replace("Mythic Unique", "Mythic");
            itemType = m.Groups["type"].Value.Trim();
        }

        int? itemPower = null, requiredLevel = null, dps = null;
        (int, int)? perHit = null;
        double? attacksPerSecond = null;
        var emptySockets = 0;
        var body = new List<TextLine>();
        var flavor = false;
        var compared = false;
        for (var i = bodyStart; i < lines.Count; i++)
        {
            var text = AffixTemplate.FixOcrLetters(lines[i].Text); // "Qua1ity", "Requires Leve1"
            if (ItemPowerLine().Match(text) is { Success: true } power)
            {
                itemPower = ParseInt(power.Groups[1].Value) + (power.Groups[2].Success ? ParseInt(power.Groups[2].Value) : 0);
                continue;
            }
            if (DpsLine().Match(text) is { Success: true } d)
            {
                dps = ParseInt(d.Groups[1].Value);
                continue;
            }
            if (PerHitLine().Match(text) is { Success: true } hit)
            {
                perHit = (ParseInt(hit.Groups[1].Value), ParseInt(hit.Groups[2].Value));
                continue;
            }
            if (AttacksLine().Match(FixOcrDigits(text)) is { Success: true } aps)
            {
                attacksPerSecond = double.Parse(aps.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }
            if (EmptySocketLine().IsMatch(text))
            {
                emptySockets++;
                continue;
            }
            // Armor's base value ("638 Armor (-2.4% Toughness)", no "+") heads the stats, before any affix; the "+# Armor"
            // affix comes later.
            if (body.Count == 0 && BaseArmorLine().IsMatch(text))
                continue;
            if (QualityLine().IsMatch(text) || UiLine().IsMatch(text))
                continue;
            if (RequiresLevelLine().Match(text) is { Success: true } level)
            {
                requiredLevel = ParseInt(level.Groups[1].Value);
                break; // footer: account binding, sell value, durability
            }
            if (PropertiesLostLine().IsMatch(text))
                compared = true;
            if (PropertiesLostLine().IsMatch(text) || ScrollDownLine().IsMatch(text))
                break; // comparison view: the rest lists the equipped item's stats, then action hints
            // A set charm's own lines end where its set begins: the set's name, its members, "Dark Pact (1/2)", then
            // "(2) Set:" and the bonuses, down to the footer. None of that is the charm's.
            if (flavor || FlavorLine().IsMatch(text) || rarity == "Set" && (SetBonusesLine().IsMatch(text) || _setNames.Any(n => text.TrimStart().StartsWith(n, StringComparison.OrdinalIgnoreCase))))
            {
                flavor = true; // a unique's quote closes its power, down to the footer
                continue;
            }
            if (rarity != "Common") // a common item has no affixes: its body is flavour text and the footer
                body.Add(lines[i]);
        }

        // Body first: item names are built from the item's affixes, so the read affixes help correct the name.
        var matched = MatchBody(body);
        // What's socketed sits last in the body: runewords ("CemQua (75/50) - Acrobatic Wind Walk ...") and gems, whose
        // effect reads like an affix ("+20 Willpower"). Taken off the affixes, so they don't count as the item's own.
        var socketed = new List<string>();
        while (matched.Count > 0 && (RuneHeader().IsMatch(matched[^1].Text) || IsGemEffect(matched[^1].Text)))
        {
            socketed.Insert(0, matched[^1].Text);
            matched.RemoveAt(matched.Count - 1);
        }
        var bodyEntries = matched.SelectMany(l => l.Matches).Select(m => m.Entry).ToHashSet();
        var resolved = string.IsNullOrEmpty(name) ? null : _names.Resolve(name, rarity, itemType, bodyEntries);
        if (resolved?.Power is { Kind: AffixKind.Unique or AffixKind.Mythic or AffixKind.Aspect } named && !bodyEntries.Contains(named)
            && MatchPower(matched, named))
            bodyEntries.Add(named);
        return new ItemTooltip
        {
            Bounds = tooltipBounds,
            Name = resolved?.Name ?? name,
            RawName = name,
            NameConfidence = resolved?.Confidence,
            BaseName = resolved?.BaseName,
            NamedPower = resolved?.Power,
            NameMatchesPower = resolved?.Power is { } p ? bodyEntries.Contains(p) : null,
            TypeLine = typeLine,
            Rarity = rarity,
            ItemType = itemType,
            Ancestral = ancestral,
            IsEquipped = lines.Take(Math.Max(0, typeIndex)).Any(l => IsHeaderNoise(l.Text)),
            Compared = compared,
            AccountBound = lines.Any(l => AccountBoundLine().IsMatch(AffixTemplate.FixOcrLetters(l.Text))),
            Truncated = lines.Any(l => ScrollDownLine().IsMatch(l.Text)),
            ItemPower = itemPower,
            RequiredLevel = requiredLevel,
            DamagePerSecond = dps,
            DamagePerHit = perHit,
            AttacksPerSecond = attacksPerSecond,
            EmptySockets = emptySockets,
            Socketed = socketed,
            Lines = matched,
            Season = catalog.Season,
        };
    }

    /// <summary>
    /// OCR splits a line where its color changes or a gap opens ("BacQue" "(50/300) -" "Selachian Fortress"): joins the
    /// pieces that share a row, left to right, and returns the lines top to bottom.
    /// </summary>
    private static List<TextLine> JoinRows(IReadOnlyList<TextLine> ocrLines)
    {
        var rows = new List<TextLine>();
        foreach (var line in ocrLines.OrderBy(l => l.Bounds.Y).ThenBy(l => l.Bounds.X))
        {
            var row = rows.FindLastIndex(r => SameRow(r.Bounds, line.Bounds));
            if (row < 0)
            {
                rows.Add(line);
                continue;
            }
            var (left, right) = rows[row].Bounds.X <= line.Bounds.X ? (rows[row], line) : (line, rows[row]);
            rows[row] = new TextLine(left.Text + " " + right.Text, left.Bounds.Union(right.Bounds));
        }
        return rows.OrderBy(l => l.Bounds.Y).ThenBy(l => l.Bounds.X).ToList();

        // Overlapping by more than half the shorter height, and side by side rather than stacked.
        static bool SameRow(PixelRect a, PixelRect b) =>
            Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y) > Math.Min(a.Height, b.Height) / 2
            && (a.X + a.Width <= b.X + 2 || b.X + b.Width <= a.X + 2);
    }

    /// <summary>
    /// The item's named power when OCR mangled it past what the templates tolerate ("HolyLight's", a dropped "10%[+]"):
    /// the run of unmatched lines that shares the most words with it, if most of them. Replaces the run with one line.
    /// </summary>
    private bool MatchPower(List<TooltipLine> lines, AffixEntry power)
    {
        var template = string.Join(" ", power.Lines);
        var want = Words(template);
        (int Start, int Count, double Score) best = (0, 0, 0);
        for (var start = 0; start < lines.Count; start++)
        {
            for (var count = 1; count <= MaxPowerLines && start + count <= lines.Count && !lines[start + count - 1].IsMatched; count++)
            {
                var got = Words(string.Join(" ", lines.Skip(start).Take(count).Select(l => l.Text)));
                var shared = want.Sum(w => Math.Min(w.Value, got.GetValueOrDefault(w.Key)));
                var score = (double)shared / Math.Max(want.Values.Sum(), got.Values.Sum());
                if (score > best.Score)
                    best = (start, count, score);
            }
        }
        if (best.Score < MinPowerOverlap)
            return false;
        var run = lines.GetRange(best.Start, best.Count);
        var text = string.Join(" ", run.Select(l => l.Text));
        var bounds = run.Aggregate(default(PixelRect), (acc, l) => acc.Union(l.Bounds));
        lines.RemoveRange(best.Start, best.Count);
        lines.Insert(best.Start, new TooltipLine(text, bounds, [new AffixMatch(power, 0, PowerValues(template, text))]));
        return true;

        static Dictionary<string, int> Words(string text) => WordPattern().Matches(AffixTemplate.FixOcrLetters(text).ToLowerInvariant())
            .GroupBy(m => m.Value).ToDictionary(g => g.Key, g => g.Count());
    }

    private const int MaxPowerLines = 12;
    private const double MinPowerOverlap = 0.6;

    /// <summary>
    /// The power's rolled values: each # follows a word ("deals # Fire Damage"), so the value is the first number after
    /// that word in the text. Empty when one can't be found, so no value is guessed.
    /// </summary>
    private static double[] PowerValues(string template, string text)
    {
        var normalizedTemplate = AffixTemplate.Normalize(template);
        var normalized = AffixTemplate.Normalize(text);
        var values = new List<double>();
        var at = 0;
        foreach (Match slot in SlotAfterWord().Matches(normalizedTemplate))
        {
            var value = new Regex($@"\b{Regex.Escape(slot.Groups[1].Value)}\b\W{{0,3}}(\d[\d,]*(?:\.\d+)?)", RegexOptions.IgnoreCase)
                .Match(normalized, at);
            if (!value.Success)
                return [];
            values.Add(double.Parse(value.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture));
            at = value.Index + value.Length;
        }
        return values.Count == normalizedTemplate.Count(c => c == '#') ? [.. values] : [];
    }

    [GeneratedRegex(@"[a-z]{2,}")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"([A-Za-z']+)\W{0,3}#")]
    private static partial Regex SlotAfterWord();

    /// <summary>
    /// Matches each line, joining it with the following lines when the affix text wrapped. The longest joined span that
    /// matches wins: "x21% Critical Strike Damage" also matches a shorter affix on its own, before its "Multiplier" line.
    /// Once a span matches, longer ones are only tried through lines that continue it (not starting with a value,
    /// sign or icon), so the next affix isn't swallowed.
    /// </summary>
    private List<TooltipLine> MatchBody(List<TextLine> body)
    {
        var result = new List<TooltipLine>();
        var i = 0;
        while (i < body.Count)
        {
            if (RuneHeader().IsMatch(body[i].Text))
            {
                // A socketed rune pair: its header, the runeword's effect, up to "(Overflow: ...)". Not in the season
                // data, so shown as read, as one line.
                var end = i + 1;
                while (end < body.Count && end - i < MaxWrappedLines && !RuneHeader().IsMatch(body[end].Text)
                       && !OverflowLine().IsMatch(body[end - 1].Text))
                    end++;
                if (OverflowLine().IsMatch(body[end - 1].Text))
                {
                    var rune = body.Skip(i).Take(end - i).ToList();
                    result.Add(new TooltipLine(LeadingIcon().Replace(string.Join(" ", rune.Select(l => l.Text)), ""),
                        rune.Aggregate(default(PixelRect), (acc, l) => acc.Union(l.Bounds)), []));
                    i = end;
                    continue;
                }
            }
            (int Span, IReadOnlyList<AffixMatch> Matches)? best = null;
            for (var span = 1; span <= MaxWrappedLines && i + span <= body.Count; span++)
            {
                if (best is not null && !Continues(body[i + span - 1].Text))
                    break;
                var matches = MatchVariants(string.Join(" ", body.Skip(i).Take(span).Select(l => l.Text)));
                if (matches.Count > 0)
                    best = (span, matches);
            }
            if (best is { } found)
            {
                var text = string.Join(" ", body.Skip(i).Take(found.Span).Select(l => l.Text));
                var bounds = body.Skip(i).Take(found.Span).Aggregate(default(PixelRect), (acc, l) => acc.Union(l.Bounds));
                result.Add(new TooltipLine(text, bounds, found.Matches));
                i += found.Span;
            }
            else
            {
                // Lines no template knows (a socketed rune's effect, a power the data lacks) wrap like any paragraph:
                // they join the unknown line above until a sentence ends there.
                if (result.Count > 0 && result[^1] is { IsMatched: false } previous && !SentenceEnd().IsMatch(previous.Text)
                    && Continues(body[i].Text))
                    result[^1] = previous with { Text = previous.Text + " " + body[i].Text, Bounds = previous.Bounds.Union(body[i].Bounds) };
                else
                    result.Add(new TooltipLine(body[i].Text, body[i].Bounds, []));
                i++;
            }
        }
        return result;
    }

    [GeneratedRegex(@"[.!?)""”]\s*$")]
    private static partial Regex SentenceEnd();

    /// <summary>Whether a line reads as the rest of the one above, rather than a new affix (which starts with a value or icon).</summary>
    private static bool Continues(string text) => NewLineStart().IsMatch(text) == false;

    [GeneratedRegex(@"^\s*(?:[+\-\d\[(]|[xX]\s*[+\d]|[^\w\s])")]
    private static partial Regex NewLineStart();

    /// <summary>"1. IO Attacks per Second": OCR reads the 1s and 0s after a decimal point as letters.</summary>
    private static string FixOcrDigits(string text) => DecimalLetters().Replace(text, m =>
        m.Groups[1].Value + "." + m.Groups[2].Value.Replace('I', '1').Replace('l', '1').Replace('|', '1').Replace('O', '0').Replace('o', '0'));

    [GeneratedRegex(@"(\d)\.\s*([\dIlO|o]{1,3})(?=\s*Attacks)")]
    private static partial Regex DecimalLetters();

    [GeneratedRegex(@"^\s*\d[\d,\s]*\s+Armor\b", RegexOptions.IgnoreCase)] // "1 ,500 Armor" as OCR spaced it
    private static partial Regex BaseArmorLine();

    /// <summary>A line ending inside a range ("... Reduction [4.0 -", "... by 50%[x] [35"): the next line holds its end.</summary>
    [GeneratedRegex(@"\[\s*\d[\d.,]*(?:\s*-)?\s*$")]
    private static partial Regex OpenRange();

    /// <summary>Where a sentence ends and an affix follows without a space: split before the "+".</summary>
    [GeneratedRegex(@"(?<=[a-z][.!?])\s*(?=\+\d)")]
    private static partial Regex GluedAffix();

    /// <summary>A range's end with its start lost ("- 9]", "- 50]%"), at the end of a line with no "[".</summary>
    [GeneratedRegex(@"\s+[-–]\s*\d[\d.,]*\]\s*%?\s*$")]
    private static partial Regex OrphanRangeTail();

    // "Account Bound", also half under the "Scroll Down" bar ("ccount Bound").
    [GeneratedRegex(@"^\W*\w?c+ount\s*Bound", RegexOptions.IgnoreCase)]
    private static partial Regex AccountBoundLine();

    // "Scroll Down" shares its row with its key icon and the footer it covers ("ZE Scroll Down lires Level 60").
    [GeneratedRegex(@"\bScroll Down\b", RegexOptions.IgnoreCase)]
    private static partial Regex ScrollDownLine();

    // A unique's flavor quote: "Kindle your inner flame..." - Paladin Justelle.
    [GeneratedRegex(@"^\s*[""“”„]")]
    private static partial Regex FlavorLine();

    // A socketed rune pair and its runeword: "BacQue (50/300) - Selachian Fortress", or without the counter OCR missed
    // ("@NeoCeh Graceful Alpha"): two rune names run together. Only taken as one when "(Overflow: ...)" ends it.
    [GeneratedRegex(@"\(\s*\d+\s*/\s*\d+\s*\)\s*-|^\W*[A-Z][a-z]+[A-Z][a-z]+\s")]
    private static partial Regex RuneHeader();

    [GeneratedRegex(@"^\s*(Weapon|Armor|Jewelry)\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SlotLabel();

    [GeneratedRegex(@"[a-z0-9]+")]
    private static partial Regex PlainWords();

    [GeneratedRegex(@"\(Overflow\b", RegexOptions.IgnoreCase)]
    private static partial Regex OverflowLine();

    /// <summary>OCR picks up the affix icon as a stray character, and aspects carry an "Imprinted:" label; try without them.</summary>
    private IReadOnlyList<AffixMatch> MatchVariants(string text)
    {
        foreach (var candidate in Variants(text))
        {
            var matches = catalog.Match(candidate);
            if (matches.Count > 0)
                return matches;
        }
        return [];
    }

    private static IEnumerable<string> Variants(string text)
    {
        yield return text;
        var stripped = LeadingIcon().Replace(text, "");
        if (stripped != text)
            yield return stripped;
        var label = AspectLabel().Replace(stripped, "");
        if (label != stripped)
            yield return label;
        var roll = MisreadFixedRoll().Replace(label, "");
        if (roll != label)
            yield return roll;
    }

    private static bool IsTypeContinuation(string text) =>
        !ItemPowerLine().IsMatch(text) && !QualityLine().IsMatch(text) && !text.Any(char.IsDigit) && text.Split(' ').Length <= 2;

    // "EQUIPPED" banner and similar labels above the header.
    private static bool IsHeaderNoise(string text) => text.Trim().ToUpperInvariant() is "EQUIPPED" or "EQUIPPED ITEM";

    /// <summary>
    /// The name from the lines above the type line, in the header's large letters: smaller text there is the game's
    /// world showing above the tooltip (an NPC's nameplate read as "Ina" before "DIRE TORMENT").
    /// </summary>
    private static string Name(IEnumerable<TextLine> header)
    {
        var lines = header.Where(l => !IsHeaderNoise(l.Text)).ToList();
        var tallest = lines.Count == 0 ? 0 : lines.Max(l => l.Bounds.Height);
        return TitleCase(string.Join(" ", lines.Where(l => l.Bounds.Height * 10 >= tallest * 7).Select(l => l.Text)));
    }

    private static string TitleCase(string text)
    {
        // The header ends with rarity marker icons that OCR reads as "*" or similar.
        text = text.TrimEnd(' ', '*', '•', '@', '#', '.', ',');
        text = LetterZero().Replace(text, "O"); // OCR reads the header font's O as 0 ("Dire T0rment"); names have no digits
        return text.Length == 0 ? text : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant()).Replace("'S ", "'s ");
    }

    [GeneratedRegex(@"(?<=[A-Za-z])0|0(?=[A-Za-z])")]
    private static partial Regex LetterZero();

    private static int ParseInt(string digits) => int.Parse(digits.Replace(",", ""), CultureInfo.InvariantCulture);

    // Not "Unique Equipped", a footer note (one may be worn at a time) that a scrolled tooltip shows without its header.
    [GeneratedRegex(@"^(?<ancestral>Ancestral\s+)?(?<rarity>Common|Magic|Rare|Legendary|Mythic Unique|Unique|Set)\s+(?<type>(?!Equipped\b).+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TypeLine();

    // "800 Item Power", or with a bonus: "540+25 Item Power".
    [GeneratedRegex(@"^(\d[\d,]*)(?:\s*\+\s*(\d+))?\s*Item Power", RegexOptions.IgnoreCase)]
    private static partial Regex ItemPowerLine();

    // Weapon base block; OCR may prefix the tree glyph ("k•") and append a comparison delta.
    [GeneratedRegex(@"^(\d[\d,]*)\s*Damage Per Second", RegexOptions.IgnoreCase)]
    private static partial Regex DpsLine();

    [GeneratedRegex(@"\[\s*(\d[\d,]*)\s*-\s*(\d[\d,\s]*?)\s*\]\s*Damage\s*per\s*Hit", RegexOptions.IgnoreCase)]
    private static partial Regex PerHitLine();

    [GeneratedRegex(@"(\d+\.\d+)\s*Attacks\s*per\s*Second", RegexOptions.IgnoreCase)]
    private static partial Regex AttacksLine();

    [GeneratedRegex(@"^\W*Empty Socket", RegexOptions.IgnoreCase)]
    private static partial Regex EmptySocketLine();

    [GeneratedRegex(@"^\s*\(\d\)\s*Set\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex SetBonusesLine();

    [GeneratedRegex(@"^\s*Properties lost when equipped", RegexOptions.IgnoreCase)]
    private static partial Regex PropertiesLostLine();

    // Interface text inside or under the tooltip: section labels, upgrade counter, class restriction, action hints.
    [GeneratedRegex(@"^\W*(Legendary Power|Unique Power|Upgrades:.*|Socket \(\d+\)|\(?[^()]{0,20}\bOnly\)?|Take|Scroll Down|Hide Comparison|Mark as (Junk|Favorite)|Unmark Item|Unequip|Link)\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex UiLine();

    [GeneratedRegex(@"Quality\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex QualityLine();

    [GeneratedRegex(@"Requires Level\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RequiresLevelLine();

    // An affix icon read as one or two symbols (possibly stuck to the text, "@+125 ...") or as a lone letter
    // followed by a space ("o +9.8% Attack Speed"), or a lone digit before a sign ("0 +863 Armor").
    [GeneratedRegex(@"^\s*(?:[^\w+\s\[(]{1,2}\s*|[A-Za-z]\s+|\d\s+(?=\+))(?=[+\dA-Zx])")]
    private static partial Regex LeadingIcon();

    // A fixed roll "+[100]" or a range "+[73 - 83]" read with its brackets as 1s ("+100 All Stats +11001",
    // "+91 Strength +173 - 831"): the line without it.
    [GeneratedRegex(@"\s+\+\s*[1l|\[]\d[\d.,]*(?:\s*-\s*\d[\d.,]*)?[1l|\]]\s*%?\s*$")]
    private static partial Regex MisreadFixedRoll();

    [GeneratedRegex(@"^\s*Imprinted:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex AspectLabel();
}
