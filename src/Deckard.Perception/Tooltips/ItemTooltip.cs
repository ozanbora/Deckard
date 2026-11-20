using Deckard.Core.Capture;
using Deckard.Core.Items;

namespace Deckard.Perception.Tooltips;

/// <summary>One logical tooltip line (OCR lines joined when the text wrapped) and the affixes it matched.</summary>
/// <param name="Text">The text as read, wrapped lines joined with spaces.</param>
/// <param name="Bounds">Where the line sits in the frame.</param>
/// <param name="Matches">Matching catalog entries; empty when the line is not a known affix.</param>
public sealed record TooltipLine(string Text, PixelRect Bounds, IReadOnlyList<AffixMatch> Matches)
{
    public bool IsMatched => Matches.Count > 0;

    /// <summary>A greater affix: its icon is the star (see <see cref="AffixIcon"/>). Only regular affixes are checked.</summary>
    public bool Greater { get; init; }

    /// <summary>The values as read, when the player corrected them (see <see cref="ReadCorrections"/>); null otherwise.</summary>
    public IReadOnlyList<double>? ReadValues { get; init; }

    /// <summary>The player looked at the values and said they're right: never flagged as misread (<see cref="ReadCheck"/>).</summary>
    public bool Confirmed { get; init; }
}

/// <summary>What Deckard read from one item tooltip.</summary>
public sealed record ItemTooltip
{
    public required PixelRect Bounds { get; init; }

    /// <summary>
    /// In a comparison, the area of all its tooltips (the hovered item's and the worn one's): what an overlay stands
    /// clear of. Null for a tooltip on its own.
    /// </summary>
    public PixelRect? Group { get; init; }

    /// <summary>Item name, corrected against the season's names when possible, e.g. "Virtuous Axolotl's Smile".</summary>
    public string? Name { get; init; }

    /// <summary>The name as OCR read it, before correction.</summary>
    public string? RawName { get; init; }

    /// <summary>How closely the OCR'd name matched <see cref="Name"/> (0..1); null when the name was not corrected.</summary>
    public double? NameConfidence { get; init; }

    /// <summary>Base item of a legendary, e.g. "Axolotl's Smile".</summary>
    public string? BaseName { get; init; }

    /// <summary>Aspect named by a legendary's name, or the power of a unique item. Known even when the power text misreads.</summary>
    public AffixEntry? NamedPower { get; init; }

    /// <summary>
    /// Whether the power implied by the name was also read in the body: true confirms both, false means the name
    /// or the body was misread (or the item comes from another season). Null when the name implies no power.
    /// </summary>
    public bool? NameMatchesPower { get; init; }

    /// <summary>The full type line, e.g. "Ancestral Legendary Ring".</summary>
    public string? TypeLine { get; init; }

    /// <summary>Common, Magic, Rare, Legendary, Unique or Mythic.</summary>
    public string? Rarity { get; init; }

    /// <summary>The item type part of the type line, e.g. "Ring" or "Chest Armor".</summary>
    public string? ItemType { get; init; }

    public bool Ancestral { get; init; }

    /// <summary>The tooltip carries the "EQUIPPED" banner: in a comparison it is the worn item, not the hovered one.</summary>
    public bool IsEquipped { get; init; }

    /// <summary>
    /// The tooltip lists "Properties lost when equipped": it's the hovered item of a comparison, and the tooltip beside
    /// it is the worn one, whether or not its "EQUIPPED" banner was read (a set charm's sits above the located frame).
    /// </summary>
    public bool Compared { get; init; }

    /// <summary>Item power including any bonus ("540+25 Item Power" is 565).</summary>
    public int? ItemPower { get; init; }

    /// <summary>The player corrected the item power; <see cref="ReadItemPower"/> is what was read.</summary>
    public bool ItemPowerEdited { get; init; }

    public int? ReadItemPower { get; init; }

    public int? RequiredLevel { get; init; }

    /// <summary>Weapons only: the base damage block under the type line.</summary>
    public int? DamagePerSecond { get; init; }
    public (int Min, int Max)? DamagePerHit { get; init; }
    public double? AttacksPerSecond { get; init; }

    public int EmptySockets { get; init; }

    /// <summary>What's socketed, as read: runewords ("CemQua (75/50) - Acrobatic Wind Walk ...") and gems' effects ("+20 Willpower").</summary>
    public IReadOnlyList<string> Socketed { get; init; } = [];

    /// <summary>"Account Bound" in the footer: the item can't be traded.</summary>
    public bool AccountBound { get; init; }

    /// <summary>The tooltip is longer than the screen ("Scroll Down"): lines below the fold weren't read.</summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// A long tooltip read again after the player scrolled it: the same item as an earlier read that was cut off. Same
    /// name and item power, or, scrolled so far its header is gone (no name, no item power), at least two of the earlier
    /// read's affixes with the same values. The lines the earlier read lacked are added after its own, the sockets and
    /// binding seen in either count, and it's whole once a read reached the end. Null when it's another item, or the
    /// earlier read was whole.
    /// </summary>
    public static ItemTooltip? MergeScrolled(ItemTooltip earlier, ItemTooltip later)
    {
        if (!earlier.Truncated || earlier.Name is null)
            return null;
        static IEnumerable<string> Affixes(ItemTooltip t) =>
            t.Lines.Where(l => l.IsMatched).Select(l => $"{l.Matches[0].Entry.Keys[0]}:{string.Join(",", l.Matches[0].Values)}");
        var sameHeader = earlier.Name == later.Name && earlier.ItemPower == later.ItemPower;
        var headless = later.Name is null && later.ItemPower is null && Affixes(later).Intersect(Affixes(earlier)).Count() >= 2;
        if (!sameHeader && !headless)
            return null;
        // A line already read: the same affix and values (OCR reads a line's text a little differently each time), or,
        // for a line no template knows, the same text.
        static string Key(TooltipLine l) => l.IsMatched ? $"{l.Matches[0].Entry.Keys[0]}:{string.Join(",", l.Matches[0].Values)}" : l.Text;
        var seen = earlier.Lines.Select(Key).ToHashSet();
        return earlier with
        {
            Lines = [.. earlier.Lines, .. later.Lines.Where(l => !seen.Contains(Key(l)))],
            EmptySockets = Math.Max(earlier.EmptySockets, later.EmptySockets),
            Socketed = earlier.Socketed.Count >= later.Socketed.Count ? earlier.Socketed : later.Socketed,
            AccountBound = earlier.AccountBound || later.AccountBound,
            RequiredLevel = earlier.RequiredLevel ?? later.RequiredLevel,
            Truncated = later.Truncated,
        };
    }

    /// <summary>Stat and affix lines between the header and the footer, in tooltip order.</summary>
    public IReadOnlyList<TooltipLine> Lines { get; init; } = [];

    /// <summary>Season and game build of the catalog the lines were matched against.</summary>
    public int Season { get; init; }
}
