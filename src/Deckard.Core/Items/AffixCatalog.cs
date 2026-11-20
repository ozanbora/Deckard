using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Deckard.Core.Items;

public enum AffixKind
{
    Affix,
    Implicit,
    Tempered,
    Aspect,
    Unique,
    Mythic,
    SetBonus,
}

/// <summary>One entry of <c>affix-templates.json</c>: an affix (or power) and the tooltip lines it produces.</summary>
public sealed record AffixEntry
{
    public required AffixKind Kind { get; init; }
    public required IReadOnlyList<string> Keys { get; init; }
    public required IReadOnlyList<long> Ids { get; init; }
    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>Aspect, unique, mythic or set name; null for regular affixes.</summary>
    public string? Name { get; init; }

    /// <summary>Magic-item name parts this affix lends, e.g. "Vigorous" / "of Vigor".</summary>
    public string? Prefix { get; init; }
    public string? Suffix { get; init; }

    public IReadOnlyList<string> ItemTypes { get; init; } = [];
    public IReadOnlyList<string> Classes { get; init; } = [];

    /// <summary>For unique and mythic powers: the item type of the unique item, e.g. "Staff".</summary>
    public string? ItemType { get; init; }

    /// <summary>For set bonuses: how many of the set's charms it takes.</summary>
    public int Required { get; init; }
}

/// <param name="Entry">The affix the line belongs to.</param>
/// <param name="LineIndex">Which of the entry's lines matched.</param>
/// <param name="Values">The rolled numbers, in line order.</param>
public sealed record AffixMatch(AffixEntry Entry, int LineIndex, IReadOnlyList<double> Values);

/// <summary>
/// The tooltip line templates for one game season (the season data the server provides).
/// Item data changes every season, so a catalog always states which season and game build it was built from.
/// </summary>
public sealed class AffixCatalog
{
    public const int SupportedSchemaVersion = 8;

    /// <summary>
    /// The data revision of optional additions this code shows: a file below it still loads, but lacks text this version
    /// would show, so the data root's season data should be produced again.
    /// </summary>
    public const int ExpectedDataRevision = 7;

    public const string FileName = "affix-templates.json";

    private readonly (AffixEntry Entry, int LineIndex, AffixTemplate Template)[] _lines;

    private AffixCatalog(TemplateFile file)
    {
        Game = file.Game;
        DataRevision = file.DataRevision;
        Season = file.Season;
        GameBuild = file.GameBuild;
        Locale = file.Locale;
        Classes = file.Classes;
        Entries = file.Entries;
        BaseNames = file.BaseNames ?? new Dictionary<string, IReadOnlyList<string>>();
        RareNamePrefixes = file.RareNameWords?.GetValueOrDefault("prefix") ?? [];
        RareNameSuffixes = file.RareNameWords?.GetValueOrDefault("suffix") ?? [];
        Items = file.Items ?? new Dictionary<string, ItemInfo>();
        Skills = file.Skills ?? new Dictionary<string, string>();
        SkillTrees = file.SkillTrees ?? new Dictionary<string, SkillTree>();
        ItemTypeClasses = file.ItemTypeClasses ?? new Dictionary<string, IReadOnlyList<string>>();
        Goods = file.Goods ?? [];
        Socketables = file.Socketables ?? new Dictionary<string, SocketableInfo>();
        ClassMechanics = file.ClassMechanics ?? new Dictionary<string, ClassMechanic>();
        Mercenaries = file.Mercenaries ?? new Dictionary<string, MercenaryInfo>();
        Paragon = file.Paragon ?? new ParagonData(new Dictionary<string, ParagonBoard>(), new Dictionary<string, ParagonNode>(), new Dictionary<string, ParagonGlyph>());
        AffixText = file.AffixText?.ToDictionary(p => long.Parse(p.Key, CultureInfo.InvariantCulture), p => p.Value) ?? [];
        _byId = [];
        foreach (var entry in Entries)
            foreach (var id in entry.Ids)
                _byId.TryAdd(id, entry);
        _lines = [.. file.Entries.SelectMany(e => e.Lines.Select((line, i) => (e, i, new AffixTemplate(line))))];
    }

    public string Game { get; }

    /// <summary>The file's revision of optional additions; 0 for files written before revisions.</summary>
    public int DataRevision { get; }

    /// <summary>The file was produced before this code's additions: produce the season data again where the server runs.</summary>
    public bool IsOutdated => DataRevision < ExpectedDataRevision;

    public int Season { get; }
    public string GameBuild { get; }
    public string Locale { get; }
    public IReadOnlyList<string> Classes { get; }
    public IReadOnlyList<AffixEntry> Entries { get; }

    /// <summary>Non-unique item names by item type ("Ring" -> "Axolotl's Smile", ...), for correcting OCR'd names.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> BaseNames { get; }

    /// <summary>Gear items by game key, e.g. "Helm_Unique_Generic_005" -> Leoric's Crown (Helm).</summary>
    public IReadOnlyDictionary<string, ItemInfo> Items { get; }

    /// <summary>Runes, gems, elixirs and boss materials: traded by name and quantity, not by their rolls. Empty in older files.</summary>
    public IReadOnlyList<GoodsInfo> Goods { get; }

    /// <summary>Seasonal socketables (e.g. soul splinters) by key, to name a build's sockets. Empty in older files.</summary>
    public IReadOnlyDictionary<string, SocketableInfo> Socketables { get; }

    /// <summary>Class mechanics a build picks, e.g. the Warlock's soul shards and fragments, by skill key. Empty in older files.</summary>
    public IReadOnlyDictionary<string, ClassMechanic> ClassMechanics { get; }

    /// <summary>The mercenaries a build hires or calls as reinforcement, by class key ("MercenaryClass_BerserkerCrone"). Empty in older files.</summary>
    public IReadOnlyDictionary<string, MercenaryInfo> Mercenaries { get; }

    /// <summary>The categories of <see cref="Goods"/>, e.g. "Rune", "Gem", "Elixir", "Material".</summary>
    public IEnumerable<string> GoodsKinds => Goods.Select(g => g.Kind).Distinct();

    /// <summary>Skill names by key, e.g. "Paladin_HolyShock_Aura" -> "Holy Light Aura".</summary>
    public IReadOnlyDictionary<string, string> Skills { get; }

    /// <summary>Each class's skill tree: what a point in each node buys.</summary>
    public IReadOnlyDictionary<string, SkillTree> SkillTrees { get; }

    /// <summary>Which classes can equip each item type ("Wand" -> Sorcerer, Necromancer); empty in older data.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ItemTypeClasses { get; }

    /// <summary>Paragon boards (all classes, by board key), what each node grants, and the glyphs.</summary>
    public ParagonData Paragon { get; }

    /// <summary>Every affix's lines by id, including those the entries leave out (aspect variants, charms, seals), for showing planned items.</summary>
    public IReadOnlyDictionary<long, AffixText> AffixText { get; }

    private readonly Dictionary<long, AffixEntry> _byId;

    /// <summary>The entry an affix or power id belongs to (ids as used by the game and the maxroll planner).</summary>
    public AffixEntry? FindById(long id) => _byId.GetValueOrDefault(id);

    /// <summary>Words rare item names are built from.</summary>
    public IReadOnlyList<string> RareNamePrefixes { get; }
    public IReadOnlyList<string> RareNameSuffixes { get; }

    /// <summary>
    /// Every entry whose template matches the line. One line can legitimately belong to several entries
    /// (e.g. "+# Fire Resistance" is a regular affix, an implicit and a tempering option); the caller decides from context.
    /// </summary>
    public IReadOnlyList<AffixMatch> Match(string line)
    {
        var normalized = AffixTemplate.Normalize(line);
        if (normalized.Length == 0)
            return [];

        var matches = new List<AffixMatch>();
        foreach (var (entry, index, template) in _lines)
        {
            if (template.TryMatchNormalized(normalized, out var values))
                matches.Add(new AffixMatch(entry, index, values));
        }
        return matches;
    }

    public static AffixCatalog Load(Stream json)
    {
        var file = JsonSerializer.Deserialize<TemplateFile>(json, JsonOptions)
            ?? throw new InvalidDataException("Affix template file is empty.");
        if (file.SchemaVersion != SupportedSchemaVersion)
            throw new InvalidDataException(
                $"Affix template schema {file.SchemaVersion} is not supported (expected {SupportedSchemaVersion}). Produce the season data again for this version.");
        return new AffixCatalog(file);
    }

    public static AffixCatalog Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>Loads <c>&lt;dataRoot&gt;/season-&lt;n&gt;/affix-templates.json</c>, or the newest season when <paramref name="season"/> is null.</summary>
    public static AffixCatalog LoadSeason(string dataRoot, int? season = null)
    {
        var chosen = season ?? AvailableSeasons(dataRoot).DefaultIfEmpty(-1).Max();
        var path = Path.Combine(dataRoot, $"season-{chosen}", FileName);
        if (chosen < 0 || !File.Exists(path))
            throw new FileNotFoundException(
                season is null
                    ? $"No season data under {dataRoot}."
                    : $"No data for season {season} under {dataRoot}.",
                path);
        return Load(path);
    }

    /// <summary>
    /// Finds <c>data/d4</c> in the current directory, the app's directory, or any of their parents, so tools and the
    /// app work from anywhere inside the repo (including bin folders).
    /// </summary>
    public static string? FindDataRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "data", "d4");
                if (Directory.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    public static IEnumerable<int> AvailableSeasons(string dataRoot) =>
        Directory.Exists(dataRoot)
            ? Directory.EnumerateDirectories(dataRoot, "season-*")
                .Select(d => int.TryParse(Path.GetFileName(d)["season-".Length..], out var n) ? n : -1)
                .Where(n => n >= 0 && File.Exists(Path.Combine(dataRoot, $"season-{n}", FileName)))
                .Order()
            : [];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record TemplateFile(
        int SchemaVersion,
        string Game,
        int Season,
        string GameBuild,
        string Locale,
        IReadOnlyList<string> Classes,
        IReadOnlyList<AffixEntry> Entries,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? BaseNames,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? RareNameWords,
        IReadOnlyDictionary<string, ItemInfo>? Items,
        IReadOnlyDictionary<string, string>? Skills,
        IReadOnlyDictionary<string, SkillTree>? SkillTrees,
        ParagonData? Paragon,
        IReadOnlyDictionary<string, AffixText>? AffixText,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? ItemTypeClasses = null,
        IReadOnlyList<GoodsInfo>? Goods = null,
        IReadOnlyDictionary<string, SocketableInfo>? Socketables = null,
        IReadOnlyDictionary<string, ClassMechanic>? ClassMechanics = null,
        IReadOnlyDictionary<string, MercenaryInfo>? Mercenaries = null,
        int DataRevision = 0);
}

/// <param name="Slots">Per # across the lines, in order: [value index, factor, decimals (-1: as needed)], or null
/// when the number isn't a rolled value.</param>
/// <param name="MagicType">0 affix, 1 legendary aspect, 2 unique power, 4 mythic power.</param>
/// <param name="Ranges">Per value index, the base roll range by item power: [[item power, min, max], ...] (raw values,
/// before masterworking); null where the value doesn't roll.</param>
public sealed record AffixText(IReadOnlyList<string> Lines, IReadOnlyList<double[]?> Slots, int MagicType, IReadOnlyList<IReadOnlyList<double[]>?>? Ranges = null)
{
    /// <summary>The lines with each # replaced by its rolled value as the game shows it ("+#% Attack Speed", [0.08] ->
    /// "+8.0% Attack Speed" when the template asks for one decimal); # stays where the value is unknown.</summary>
    /// <param name="rangesAt">Also show each value's roll range the way the game's advanced tooltips do, for an item
    /// of this item power (0: only ranges that don't depend on item power). A regular affix gets them at the end of its
    /// line ("+8.0% Cooldown Reduction [5.0 - 8.0]%"), an aspect right after the number ("by 2.9 [1.0 - 3.0] Seconds").
    /// Unique and mythic powers never get one: their rolls don't follow the base formulas.</param>
    /// <param name="rangeScale">2 on two-handed weapons, which roll double values.</param>
    public IReadOnlyList<string> Fill(IReadOnlyList<double> values, int? rangesAt = null, double rangeScale = 1)
    {
        var slot = 0;
        var showRanges = rangesAt is not null && MagicType is not (2 or 4);
        return [.. Lines.Select(line =>
        {
            var trailing = new StringBuilder();
            var filled = Regex.Replace(line, "#%?", m =>
            {
                var s = slot < Slots.Count ? Slots[slot] : null;
                slot++;
                var unit = m.Value[1..];
                if (s is not [var index, var factor, var decimals] || index >= values.Count)
                    return m.Value;
                var text = Format(values[(int)index] * factor, decimals) + unit;
                if (!showRanges || RangeAt((int)index, rangesAt!.Value) is not [var low, var high])
                    return text;
                var range = low == high
                    ? $"[{Format(low * factor * rangeScale, decimals)}]{unit}"
                    : $"[{Format(low * factor * rangeScale, decimals)} - {Format(high * factor * rangeScale, decimals)}]{unit}";
                if (MagicType != 0)
                    return $"{text} {range}";
                trailing.Append(trailing.Length == 0 ? " " : "").Append(range);
                return text;
            });
            return filled + trailing;
        })];
    }

    /// <summary>[min, max] for a value at an item power, or null. Power 0 takes a range only when it never changes.</summary>
    public double[]? RangeAt(int index, int itemPower)
    {
        if (Ranges is null || index >= Ranges.Count || Ranges[index] is not { Count: > 0 } tiers)
            return null;
        if (itemPower <= 0)
            return tiers.Count == 1 ? tiers[0][1..] : null;
        return (tiers.LastOrDefault(t => t[0] <= itemPower) ?? tiers[0])[1..];
    }

    private static string Format(double value, double decimals) => decimals >= 0
        ? value.ToString("N" + (int)decimals, CultureInfo.InvariantCulture)
        : value.ToString("#,0.##", CultureInfo.InvariantCulture);
}

/// <param name="Icon">Icon id (icons/&lt;id&gt;.webp in the season data), 0 if none.</param>
/// <param name="TwoHanded">Two-handed weapons roll double affix values.</param>
/// <param name="Unique">The item is unique (runewords included), whatever its affixes say.</param>
/// <param name="Set">A set charm's set, as the set bonus entries name it; null otherwise and in older files.</param>
/// <param name="MagicType">The item's own rarity: 1 legendary, 2 unique, 3 set, 4 mythic; 0 otherwise and in older files.</param>
/// <param name="SetDiscount">How many fewer charms set bonuses take with this item (a seal's power; never below 2 charms).</param>
public sealed record ItemInfo(string Name, string Type, long Icon = 0, bool TwoHanded = false, bool Unique = false, string? Set = null, int MagicType = 0,
    int SetDiscount = 0);

/// <summary>A seasonal socketable, e.g. a soul splinter: its name, icon and what it does.</summary>
/// <param name="Lines">Its tooltip lines: its own bonus, then its effect per slot ("Jewelry: …"). Empty in older files.</param>
public sealed record SocketableInfo(string Name, long Icon = 0, IReadOnlyList<string>? Lines = null);

/// <summary>A class mechanic a build picks, e.g. "Legion Shard" and what it does.</summary>
public sealed record ClassMechanic(string Name, string Desc = "");

/// <summary>A mercenary: its name ("Varyana"), the skill each node of its tree gives, and its skills' text.</summary>
/// <param name="Nodes">Tree node id (as planners pick them) to the skill key it gives.</param>
/// <param name="Skills">Its skills, active and passive, by key: name and what it does.</param>
/// <param name="Tree">Its skill tree, laid out as a class's; null in older files.</param>
public sealed record MercenaryInfo(string Name, IReadOnlyDictionary<int, string> Nodes, IReadOnlyDictionary<string, ClassMechanic> Skills, SkillTree? Tree = null)
{
    /// <summary>Tree nodes in the order the tree opens them, top down from its root (Cleave before Taste of Flesh); as given without a tree.</summary>
    public IReadOnlyList<int> InTreeOrder(IEnumerable<int> nodes) =>
        Tree is null ? [.. nodes] : [.. nodes.OrderBy(n => Tree.Nodes.FirstOrDefault(x => x.Node == n)?.Y ?? int.MaxValue)];
}

/// <summary>A rune, gem, elixir or boss material, listed by its <paramref name="Kind"/> and name.</summary>
/// <param name="Lines">What it does: a rune's effect and its Offering, a gem's effect per slot. Empty in older files.</param>
/// <param name="Ritual">A rune's side of a runeword: true for a Rune of Ritual (gains Offering), false for one of Invocation
/// (spends it); null for other goods and in older files.</param>
/// <param name="Offering">The Offering a Ritual rune gains per trigger, or an Invocation rune needs per cast.</param>
public sealed record GoodsInfo(string Key, long Id, string Name, string Kind, long Icon = 0, IReadOnlyList<string>? Lines = null,
    bool? Ritual = null, int Offering = 0);

/// <param name="Kind">"skill", "modifier" or "passive" (hold points), "cluster" (hub on the trunk) or "junction" (branch point).</param>
/// <param name="Desc">Tooltip text with the values the data states directly; rank-dependent numbers show as #.</param>
/// <param name="DescByRank">The tooltip at each rank from 1, with rank-dependent values (damage as a share of weapon
/// damage) filled in; null when no value depends on the rank. Values that depend on the character stay as #.</param>
/// <param name="X">Position in the game's tree coordinates; the Basic skills have the lowest y and the game draws y downward.</param>
public sealed record SkillNode(int Node, string Kind, string Skill, string Name, int MaxRanks, int RequiredLevel, int X, int Y, long Icon, string? Desc = null, IReadOnlyList<string>? DescByRank = null)
{
    /// <summary>The tooltip at a rank (rank 1 for an unused node).</summary>
    public string? DescAt(int rank) => DescByRank is { Count: > 0 } byRank ? byRank[Math.Clamp(rank, 1, byRank.Count) - 1] : Desc;
}

/// <param name="Corners">Elbow points between the two nodes, as [x, y] pairs.</param>
public sealed record SkillLink(int A, int B, IReadOnlyList<int[]> Corners);

public sealed record SkillTree(IReadOnlyList<SkillNode> Nodes, IReadOnlyList<SkillLink> Links);

/// <param name="Nodes">Node keys (see <see cref="ParagonData.Nodes"/>) row by row, <paramref name="Width"/> per row; null where the board has no node.</param>
public sealed record ParagonBoard(string Name, int Width, IReadOnlyList<string?> Nodes);

/// <param name="Kind">"normal", "magic", "rare", "legendary", "gate", "socket" (glyph) or "start".</param>
/// <param name="Lines">What the node grants; a rare node's last line is its bonus for enough of a core stat.</param>
/// <param name="IconIsMask">The icon is a white shape the game tints by rarity, rather than a finished image.</param>
public sealed record ParagonNode(string Name, string Kind, IReadOnlyList<string> Lines, long Icon, bool IconIsMask);

/// <param name="Lines">The glyph's effects; level-dependent numbers show as #.</param>
public sealed record ParagonGlyph(string Name, IReadOnlyList<string> Lines);

public sealed record ParagonData(
    IReadOnlyDictionary<string, ParagonBoard> Boards, IReadOnlyDictionary<string, ParagonNode> Nodes, IReadOnlyDictionary<string, ParagonGlyph> Glyphs);
