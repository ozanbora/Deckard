using Deckard.Core.Builds;
using Deckard.Core.Items;
using Deckard.Perception.Tooltips;

namespace Deckard.Perception.Builds;

/// <summary>
/// An item the player wears, as read from a tooltip with the "EQUIPPED" banner: its type, name and the ids of every
/// affix, aspect and power read on it. Kept per account, so it holds no screen positions or OCR text.
/// </summary>
public sealed record WornItem(string ItemType, string? Name, string? Rarity, IReadOnlyList<long> Ids, DateTimeOffset ReadAt)
{
    public static WornItem? From(ItemTooltip tooltip, DateTimeOffset readAt) => tooltip.ItemType is { } type
        ? new WornItem(type, tooltip.Name, tooltip.Rarity,
            [.. tooltip.Lines.SelectMany(l => l.Matches).Select(m => m.Entry).Append(tooltip.NamedPower).OfType<AffixEntry>().SelectMany(e => e.Ids).Distinct()],
            readAt)
        : null;

    /// <summary>The same item read again: same type, name and affixes (values aside, which OCR can misread).</summary>
    public bool SameAs(WornItem other) =>
        string.Equals(ItemType, other.ItemType, StringComparison.OrdinalIgnoreCase) && Name == other.Name && Ids.ToHashSet().SetEquals(other.Ids);
}

/// <summary>Something to do to a worn item to reach the build's: <see cref="Affix"/> is what to enchant, temper or imprint.</summary>
/// <param name="Key">Stable for the slot and task, so a player can tick it off.</param>
public sealed record CraftTask(int Slot, CraftKind Kind, AffixEntry? Affix, string Key);

public enum CraftKind
{
    /// <summary>The item is too far from the build's: find another (the reason is the comparison's verdict).</summary>
    Replace,
    /// <summary>Imprint the build's aspect at the Occultist.</summary>
    Imprint,
    /// <summary>Re-roll one affix into the build's at the Occultist.</summary>
    Enchant,
    /// <summary>Add the build's tempered affix at the Blacksmith.</summary>
    Temper,
}

/// <summary>The player's worn gear against a build: which worn item fills which slot, how well, and what's left to craft.</summary>
public static class Gear
{
    /// <summary>
    /// The worn items after reading <paramref name="item"/>: it replaces the same item read before, and of each item type
    /// only as many are kept as the build has slots for (two rings, say), the oldest going first. Not knowing which slot
    /// a ring was in, the two most recently read rings are taken to be the two worn.
    /// </summary>
    public static List<WornItem> Remember(IReadOnlyList<WornItem> worn, WornItem item, BuildVariant? variant, AffixCatalog catalog)
    {
        var kept = worn.Where(w => !w.SameAs(item)).Append(item).ToList();
        var slots = variant?.Items.Values.Count(p => SameType(p, item.ItemType, catalog)) ?? 0;
        var ofType = kept.Where(w => string.Equals(w.ItemType, item.ItemType, StringComparison.OrdinalIgnoreCase)).OrderBy(w => w.ReadAt).ToList();
        foreach (var old in ofType.Take(Math.Max(0, ofType.Count - Math.Max(1, slots))))
            kept.Remove(old);
        return kept;
    }

    /// <summary>Puts each worn item in the build slot it fits best: the best pairs first, one item per slot.</summary>
    public static Dictionary<int, (WornItem Item, ItemComparison Fit)> Assign(IReadOnlyList<WornItem> worn, BuildVariant variant, AffixCatalog catalog)
    {
        var pairs = from item in worn
                    from planned in variant.Items.Values
                    where SameType(planned, item.ItemType, catalog)
                    select (item, Fit: BuildMatcher.CompareWith(item.Name, item.Ids, planned, catalog));
        var assigned = new Dictionary<int, (WornItem, ItemComparison)>();
        var used = new HashSet<WornItem>();
        foreach (var (item, fit) in pairs.OrderByDescending(p => p.Fit.Score).ThenByDescending(p => p.item.ReadAt))
            if (!assigned.ContainsKey(fit.Planned.Slot) && used.Add(item))
                assigned[fit.Planned.Slot] = (item, fit);
        return assigned;
    }

    /// <summary>
    /// What's left to do on a worn item to make it the build's: imprint its aspect, enchant the one missing affix, temper
    /// its tempered affixes, or, when that can't get there, replace it.
    /// </summary>
    public static IEnumerable<CraftTask> Tasks(WornItem item, ItemComparison fit, AffixCatalog catalog)
    {
        var slot = fit.Planned.Slot;
        if (fit.Verdict is not { } verdict)
            yield break;
        if (verdict.Verdict == ItemVerdict.Salvage)
        {
            yield return new CraftTask(slot, CraftKind.Replace, null, $"{slot}:replace");
            yield break;
        }
        if (fit.AspectMatches == false && fit.Planned.Aspects.Select(a => catalog.FindById(a.Id)).OfType<AffixEntry>().FirstOrDefault() is { } aspect)
            yield return new CraftTask(slot, CraftKind.Imprint, aspect, $"{slot}:imprint:{aspect.Ids[0]}");
        foreach (var missing in fit.Missing)
            yield return new CraftTask(slot, CraftKind.Enchant, missing, $"{slot}:enchant:{missing.Ids[0]}");
        var read = item.Ids.ToHashSet();
        foreach (var tempered in fit.Planned.Tempered.Select(a => catalog.FindById(a.Id)).OfType<AffixEntry>().Where(e => !e.Ids.Any(read.Contains)))
            yield return new CraftTask(slot, CraftKind.Temper, tempered, $"{slot}:temper:{tempered.Ids[0]}");
    }

    private static bool SameType(PlannedItem planned, string itemType, AffixCatalog catalog) =>
        catalog.Items.TryGetValue(planned.ItemKey, out var info) && string.Equals(info.Type, itemType, StringComparison.OrdinalIgnoreCase);
}
