using System.Text.RegularExpressions;
using Deckard.Core.Items;

namespace Deckard.Core.Builds;

/// <summary>
/// Turns what build sites write (slot names, item, aspect, affix, gem and board names) into the game ids of the
/// season's catalog, so a d4builds or Icy Veins build reads like a maxroll one. Names a site writes that the catalog
/// doesn't know are left out rather than guessed.
/// </summary>
internal sealed partial class ImportNames(AffixCatalog catalog)
{
    public AffixCatalog Catalog { get; } = catalog;

    private readonly ILookup<(AffixKind, string), AffixEntry> _byLine = catalog.Entries
        .SelectMany(e => e.Lines.Select(l => (Key: (e.Kind, Norm(l)), Entry: e))).ToLookup(x => x.Key, x => x.Entry);
    private readonly ILookup<string, AffixEntry> _byName = catalog.Entries.Where(e => e.Name is not null).ToLookup(e => Norm(e.Name!));
    private readonly Dictionary<string, (AffixEntry Entry, long Id)> _byKey = catalog.Entries
        .SelectMany(e => e.Keys.Zip(e.Ids, (k, id) => (k, e, id))).DistinctBy(x => x.k).ToDictionary(x => x.k, x => (x.e, x.id));

    /// <summary>The game's slot number (see <see cref="PlannedItem"/>) for a site's slot name, or -1.</summary>
    public static int Slot(string name)
    {
        var s = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]", "");
        if (s.StartsWith("talismancharm", StringComparison.Ordinal) && int.TryParse(s["talismancharm".Length..], out var charm))
            return 20 + charm;
        return s switch
        {
            "helm" or "helmet" => 4,
            "chest" or "chestarmor" or "chestplate" => 5,
            "offhand" => 6,
            "weapon" or "mainweapon" or "mainhand" => 7,
            "gloves" => 13,
            "pants" or "legs" => 14,
            "boots" => 15,
            "ring1" => 16,
            "ring2" => 17,
            "amulet" => 18,
            "seal" or "talismanseal" => 20,
            _ when s.StartsWith("bludgeoning", StringComparison.Ordinal) => 8,
            _ when s.StartsWith("slashing", StringComparison.Ordinal) => 9,
            _ when s.StartsWith("ranged", StringComparison.Ordinal) => 10,
            _ when s.StartsWith("dualwield", StringComparison.Ordinal) => s.EndsWith('2') ? 12 : 11,
            _ => -1,
        };
    }

    /// <summary>The item type a slot holds, where only one fits (weapons vary by build, so they have none).</summary>
    public static string? SlotType(int slot) => slot switch
    {
        4 => "Helm", 5 => "Chest Armor", 13 => "Gloves", 14 => "Pants", 15 => "Boots", 16 or 17 => "Ring", 18 => "Amulet",
        _ => null,
    };

    /// <summary>
    /// A catalog item key for a planned item: the site's key when the catalog has it (or has it without a class
    /// suffix, as Icy Veins writes "Gloves_Normal_Generic_001_warlock"), else a base of the given type, else "".
    /// A build fits read items by their type, so a stand-in base of the right type is enough.
    /// </summary>
    public string Base(string? key, string? type)
    {
        if (key is not null && Catalog.Items.ContainsKey(key))
            return key;
        if (key is not null && TrailingWord().Replace(key, "") is var plain && Catalog.Items.ContainsKey(plain))
            return plain;
        if (type is null)
            return "";
        return Catalog.Items.Where(p => !p.Value.Unique && string.Equals(p.Value.Type, type, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Key.Contains("_Generic_", StringComparison.Ordinal) ? 0 : 1).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key).FirstOrDefault() ?? "";
    }

    /// <summary>
    /// The key of the item with this name, of <paramref name="type"/> first and then uniques (a unique, its charm
    /// and a base can share a name).
    /// </summary>
    public string? Item(string name, string? type = null) => Catalog.Items
        .Where(p => string.Equals(p.Value.Name, name, StringComparison.OrdinalIgnoreCase))
        .OrderBy(p => type is not null && string.Equals(p.Value.Type, type, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(p => p.Value.Unique ? 0 : 1).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).FirstOrDefault();

    /// <summary>A legendary aspect or unique power by its name, e.g. "Aspect of Ignition" or "Leoric's Crown".</summary>
    public AffixEntry? Power(string name) => _byName[Norm(name)].FirstOrDefault(e => e.Kind is AffixKind.Aspect or AffixKind.Unique or AffixKind.Mythic);

    /// <summary>
    /// An affix by the words of its line, numbers left out ("Vulnerable Damage Multiplier" for
    /// "x#% Vulnerable Damage Multiplier"), preferring one that rolls on <paramref name="itemType"/>.
    /// </summary>
    public AffixEntry? Line(string text, AffixKind? kind, string? itemType = null)
    {
        var norm = Norm(text);
        var found = kind is { } k ? _byLine[(k, norm)] : Enum.GetValues<AffixKind>().SelectMany(any => _byLine[(any, norm)]);
        return found.OrderBy(e => itemType is not null && e.ItemTypes.Contains(itemType, StringComparer.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
    }

    /// <summary>An affix by a game id ("2602170") or key ("Talisman_SealAffix_Normal_Damage_All"), as Icy Veins writes them.</summary>
    public PlannedAffix? Affix(string idOrKey)
    {
        if (long.TryParse(idOrKey, out var id))
            return Catalog.FindById(id) is { } entry ? new PlannedAffix(id, IsGreater(entry, id)) : null;
        return _byKey.TryGetValue(idOrKey, out var hit) ? new PlannedAffix(hit.Id, IsGreater(hit.Entry, hit.Id)) : null;
    }

    public static PlannedAffix Planned(AffixEntry entry, bool greater = false) => new(entry.Ids[0], greater);

    private static bool IsGreater(AffixEntry entry, long id) =>
        entry.Keys.Zip(entry.Ids).Any(p => p.Second == id && p.First.Contains("Greater", StringComparison.OrdinalIgnoreCase));

    /// <summary>A gem, rune or other socketable by name, as the key maxroll writes in sockets.</summary>
    public string? Socket(string name) =>
        Catalog.Goods.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))?.Key
        ?? Catalog.Socketables.FirstOrDefault(p => string.Equals(p.Value.Name, name, StringComparison.OrdinalIgnoreCase)).Key;

    /// <summary>A class's skill (a key of <see cref="AffixCatalog.Skills"/>) by its name; sites may add "-v2" to reworked ones.</summary>
    public string? Skill(string className, string name)
    {
        var plain = VersionSuffix().Replace(name, "");
        var own = Catalog.SkillTrees.GetValueOrDefault(className)?.Nodes.Select(n => n.Skill).ToHashSet() ?? [];
        return Catalog.Skills.Where(p => string.Equals(p.Value, plain, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => own.Contains(p.Key) ? 0 : 1).Select(p => p.Key).FirstOrDefault();
    }

    public int ClassIndex(string className) =>
        Catalog.Classes.ToList().FindIndex(c => string.Equals(c, className, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A class's paragon board by its name in letters only ("demonicspicules", "demonic-spicules"); the start board
    /// is the one holding the start node, whatever a site calls it.
    /// </summary>
    public (string Id, ParagonBoard Board)? Board(string className, string name, bool start)
    {
        var boards = Catalog.Paragon.Boards.Where(p => className.StartsWith(p.Key.Split('_') is [_, var c, ..] ? c : "\0", StringComparison.OrdinalIgnoreCase));
        var hit = start
            ? boards.FirstOrDefault(p => p.Value.Nodes.Any(n => n is not null && Catalog.Paragon.Nodes.GetValueOrDefault(n)?.Kind == "start"))
            : boards.FirstOrDefault(p => Letters(p.Value.Name) == Letters(name));
        return hit.Key is null ? null : (hit.Key, hit.Value);
    }

    /// <summary>A glyph's key by its name ("Hellforge", or Icy Veins' "hellforge").</summary>
    public string? Glyph(string name) => Catalog.Paragon.Glyphs.FirstOrDefault(p => Letters(p.Value.Name) == Letters(name)).Key;

    public static string Letters(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z]", "");

    /// <summary>A line's words: numbers, signs and bracketed notes out, and a leading "x" (multipliers) or "to" (skill ranks) dropped.</summary>
    private static string Norm(string s)
    {
        s = Regex.Replace(s.ToLowerInvariant(), @"\[[^\]]*\]", " ");
        s = string.Join(' ', Regex.Replace(s, "[^a-z' ]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return LeadingWord().Replace(s, "");
    }

    [GeneratedRegex("^(x|to) ")]
    private static partial Regex LeadingWord();

    [GeneratedRegex("_[a-z]+$")]
    private static partial Regex TrailingWord();

    [GeneratedRegex(@"-v\d+$")]
    private static partial Regex VersionSuffix();
}
