using Deckard.Core.Builds;
using Deckard.Core.Items;
using Deckard.Perception.Tooltips;

namespace Deckard.Perception.Builds;

public enum StashCall
{
    /// <summary>It is a build's item already.</summary>
    Keep,
    /// <summary>A build's item after a craft (enchant, imprint).</summary>
    Craft,
    /// <summary>No build wants it, but someone else might: worth a price check.</summary>
    Sell,
    /// <summary>No build wants it and it can't, or isn't worth trying to, be traded.</summary>
    Salvage,
}

/// <param name="Build">The build it's for (Keep, Craft): its name in the player's library.</param>
/// <param name="Fit">How it compares with that build's item for the slot.</param>
public sealed record StashVerdict(StashCall Call, string? Build, ItemComparison? Fit);

/// <summary>Sorts an item read from the stash against every build in the player's library.</summary>
public static class StashSort
{
    /// <summary>
    /// Keep or craft it for the build it fits best (keeping beats crafting, then the better fit); otherwise sell it when it
    /// can be traded and is worth a look (ancestral, or a unique or mythic), else salvage it.
    /// </summary>
    public static StashVerdict Judge(ItemTooltip item, IReadOnlyList<(string Name, BuildVariant Variant)> builds, AffixCatalog catalog)
    {
        var best = builds
            .Select(b => (b.Name, Fit: BuildMatcher.Compare(item, b.Variant, catalog)))
            .Where(b => b.Fit?.Verdict?.Verdict is ItemVerdict.Keep or ItemVerdict.Craft)
            .OrderBy(b => b.Fit!.Verdict!.Value.Verdict)
            .ThenByDescending(b => b.Fit!.Score)
            .FirstOrDefault();
        if (best.Fit is { } fit)
            return new StashVerdict(fit.Verdict!.Value.Verdict == ItemVerdict.Keep ? StashCall.Keep : StashCall.Craft, best.Name, fit);
        var tradeable = !item.AccountBound && (item.Ancestral || item.Rarity is "Unique" or "Mythic");
        return new StashVerdict(tradeable ? StashCall.Sell : StashCall.Salvage, null, null);
    }
}
