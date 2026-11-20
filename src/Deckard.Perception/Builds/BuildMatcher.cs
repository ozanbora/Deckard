using Deckard.Core.Builds;
using Deckard.Core.Items;
using Deckard.Perception.Tooltips;

namespace Deckard.Perception.Builds;

/// <param name="Planned">The build's item for the slot that fits best.</param>
/// <param name="PlannedName">Its name, e.g. "Stone of Jordan" or a legendary base.</param>
/// <param name="Score">0..1: share of the planned affixes (and aspect, counted double) found on the item;
/// for a planned unique, 0 unless it is that unique.</param>
/// <param name="Found">Planned affixes read on the item.</param>
/// <param name="Missing">Planned affixes not read on the item.</param>
/// <param name="AspectMatches">Whether the planned aspect is on the item; null when the slot plans no aspect.</param>
/// <param name="SameUnique">Whether the item is the planned unique; null when the slot plans no unique.</param>
public sealed record ItemComparison(
    PlannedItem Planned, string PlannedName, double Score,
    IReadOnlyList<AffixEntry> Found, IReadOnlyList<AffixEntry> Missing, bool? AspectMatches, bool? SameUnique)
{
    /// <summary>
    /// What to do with the item, in a word and a reason. The Occultist can re-roll one affix (enchanting) and imprint an
    /// aspect, so an item one affix and/or the aspect short of the plan is worth crafting. Null when the slot's plan has
    /// nothing Deckard can compare (affixes outside the season's data, no aspect or unique).
    /// </summary>
    public (ItemVerdict Verdict, string Reason)? Verdict => (SameUnique, Missing.Count, AspectMatches) switch
    {
        (false, _, _) => (ItemVerdict.Salvage, $"Not {PlannedName}, the build's unique for this slot."),
        (true, 0, _) => (ItemVerdict.Keep, $"{PlannedName}, the build's unique, with the affixes it wants."),
        (true, _, _) => (ItemVerdict.Keep, $"{PlannedName}, the build's unique; enchanting can fix one affix."),
        _ when Found.Count + Missing.Count == 0 && AspectMatches is null => null,
        (_, 0, not false) => (ItemVerdict.Keep, "Has everything the build wants here."),
        (_, 1, not false) => (ItemVerdict.Craft, "One affix short: enchant it at the Occultist."),
        (_, 0, false) => (ItemVerdict.Craft, "Has the affixes: imprint the build's aspect at the Occultist."),
        (_, 1, false) => (ItemVerdict.Craft, "One affix and the aspect short: enchant and imprint at the Occultist."),
        _ => (ItemVerdict.Salvage, $"Missing {Missing.Count} of the build's affixes."),
    };
}

public enum ItemVerdict
{
    Keep,
    Craft,
    Salvage,
}

/// <summary>Compares a read item with the build's item for the same slot.</summary>
public static class BuildMatcher
{
    private const double AspectWeight = 2;

    /// <summary>Null when the build plans nothing of this item type (or the type wasn't read).</summary>
    public static ItemComparison? Compare(ItemTooltip item, BuildVariant variant, AffixCatalog catalog)
    {
        if (item.ItemType is null)
            return null;
        // Compare by game id: an entry carries every id of its affix (normal and greater variants).
        var read = item.Lines.SelectMany(l => l.Matches).Select(m => m.Entry).Append(item.NamedPower).OfType<AffixEntry>()
            .SelectMany(e => e.Ids).ToHashSet();
        return Best(item.ItemType, item.Name, read, variant, catalog);
    }

    /// <summary>
    /// The same comparison for an item listed for trade, from its type, name, aspect or unique power and affix ids.
    /// Null when the build plans nothing of this item type.
    /// </summary>
    public static ItemComparison? CompareListed(
        string itemType, string name, long? aspectId, IEnumerable<long> affixIds, BuildVariant variant, AffixCatalog catalog)
    {
        // A listing names each affix by one id; widen to all of the entry's ids, as a read tooltip has them.
        var ids = aspectId is { } aspect ? affixIds.Append(aspect) : affixIds;
        var read = ids.SelectMany(id => catalog.FindById(id)?.Ids ?? [id]).ToHashSet();
        return Best(itemType, name, read, variant, catalog);
    }

    /// <summary>A worn item (by name and the ids read on it) against one planned item, for <see cref="Gear.Assign"/>.</summary>
    public static ItemComparison CompareWith(string? name, IEnumerable<long> readIds, PlannedItem planned, AffixCatalog catalog) =>
        Score(name, readIds.ToHashSet(), planned, catalog);

    /// <summary>
    /// Rings fill two slots and weapons can sit in several: compare against each and keep the best fit. A weapon or
    /// off-hand of a type the guide doesn't use (an axe and a focus where it has a two-handed sword) goes against the
    /// guide's weapons: they're what it would replace.
    /// </summary>
    private static ItemComparison? Best(string itemType, string? name, HashSet<long> read, BuildVariant variant, AffixCatalog catalog)
    {
        var planned = variant.Items.Values
            .Where(p => catalog.Items.TryGetValue(p.ItemKey, out var info) && string.Equals(info.Type, itemType, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (planned.Count == 0 && IsWeapon(itemType, catalog))
            planned = [.. variant.Items.Values.Where(p => p.Slot is >= FirstWeaponSlot and <= LastWeaponSlot)];
        return planned.Select(p => Score(name, read, p, catalog)).MaxBy(c => c.Score);
    }

    /// <summary>The game's weapon slots: 6 off-hand, 7 main hand, 8-12 the other weapons (see <see cref="PlannedItem"/>).</summary>
    private const int FirstWeaponSlot = 6, LastWeaponSlot = 12;

    /// <summary>A weapon or off-hand type: the game keys those items by their hands ("1HAxe_…", "1HFocus_…", "2HSword_…").</summary>
    private static bool IsWeapon(string itemType, AffixCatalog catalog) =>
        catalog.Items.Any(i => string.Equals(i.Value.Type, itemType, StringComparison.OrdinalIgnoreCase)
            && (i.Key.StartsWith("1H", StringComparison.Ordinal) || i.Key.StartsWith("2H", StringComparison.Ordinal)));

    private static ItemComparison Score(string? name, HashSet<long> read, PlannedItem planned, AffixCatalog catalog)
    {
        bool IsRead(AffixEntry e) => e.Ids.Any(read.Contains);

        // Affixes our data doesn't know (e.g. runeword or charm pools) can't be read, so they don't count.
        var wanted = planned.Affixes.Select(a => catalog.FindById(a.Id)).OfType<AffixEntry>().Distinct().ToList();
        var found = wanted.Where(IsRead).ToList();
        var missing = wanted.Where(e => !IsRead(e)).ToList();
        var affixRatio = wanted.Count == 0 ? 0 : (double)found.Count / wanted.Count;
        var plannedName = catalog.Items[planned.ItemKey].Name;

        if (planned.ItemKey.Contains("_Unique_", StringComparison.Ordinal) || planned.ItemKey.StartsWith("Runeword_", StringComparison.Ordinal))
        {
            var same = string.Equals(name, plannedName, StringComparison.OrdinalIgnoreCase);
            return new ItemComparison(planned, plannedName, same ? 0.5 + 0.5 * affixRatio : 0, found, missing, null, same);
        }

        var aspects = planned.Aspects.Select(a => catalog.FindById(a.Id)).OfType<AffixEntry>().ToList();
        bool? aspectMatches = aspects.Count == 0 ? null : aspects.Any(IsRead);
        var total = wanted.Count + (aspects.Count > 0 ? AspectWeight : 0);
        var score = total == 0 ? 0 : (found.Count + (aspectMatches == true ? AspectWeight : 0)) / total;
        return new ItemComparison(planned, plannedName, score, found, missing, aspectMatches, null);
    }
}
