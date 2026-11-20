using Deckard.Core.Items;
using Deckard.Core.Trade;
using Deckard.Perception.Tooltips;

namespace Deckard.Perception.Trade;

/// <summary>Turns an item Deckard read into a listing: its type, power, affix lines (by catalog id) and aspect.</summary>
public static class TooltipListing
{
    /// <summary>The listing, or null when the tooltip lacks what a listing needs (type, rarity, item power).</summary>
    /// <remarks>Values are as the tooltip shows them ("8.5" for 8.5%).</remarks>
    public static NewListing? From(ItemTooltip item, Realm realm, long? priceGold)
    {
        if (item.ItemType is not { } type || item.Rarity is not { } rarity || item.ItemPower is not { } power || item.AccountBound)
            return null; // an account-bound item can't change hands
        var affixes = new List<ListedAffix>();
        foreach (var line in item.Lines)
        {
            // A line can match several entries ("+#% Fire Resistance" is an affix, an implicit and a temper); take one
            // that rolls on this item type.
            var match = line.Matches.FirstOrDefault(m => m.Entry.Kind is AffixKind.Affix or AffixKind.Implicit or AffixKind.Tempered
                && (m.Entry.ItemTypes.Count == 0 || m.Entry.ItemTypes.Contains(type)) && m.Values.Count > 0);
            if (match is null || affixes.Any(a => a.Id == match.Entry.Ids[0]))
                continue;
            var slot = match.Entry.Kind switch { AffixKind.Implicit => AffixSlot.Implicit, AffixKind.Tempered => AffixSlot.Tempered, _ => AffixSlot.Affix };
            affixes.Add(new ListedAffix(match.Entry.Ids[0], match.Values[0], line.Greater, slot));
        }
        var aspect = item.NamedPower is { Kind: AffixKind.Aspect or AffixKind.Unique or AffixKind.Mythic } named ? named.Ids[0] : (long?)null;
        return new NewListing(realm, type, item.Name ?? item.BaseName ?? type, rarity, power, item.Ancestral, aspect, affixes, priceGold);
    }

    /// <summary>
    /// Items like this one, for a price check: the same type, rarity and aspect or unique power, and up to three of its
    /// affixes (greater ones only as greater); more would rarely match anything.
    /// </summary>
    public static ListingSearch Similar(NewListing listing) => new(
        listing.Realm, listing.ItemType, listing.Rarity, AspectId: listing.AspectId,
        Affixes: [.. listing.Affixes.Where(a => a.Slot == AffixSlot.Affix).Take(3).Select(a => new AffixFilter(a.Id, Greater: a.Greater))]);
}
