using Deckard.Core.Items;
using Deckard.Core.Trade;

namespace Deckard.Trade.Api;

/// <summary>
/// Checks a new listing against the season's item data before it is stored: the client is Deckard, but anyone can
/// send requests, so every id and value is verified here. Item types, affix ids and powers come from the season's
/// affix-templates.json; nothing about items is hard-coded.
/// </summary>
public sealed class ListingRules(AffixCatalog catalog)
{
    public const int MaxAffixLines = 8;          // implicits, affixes and tempers on the biggest items
    public const long MaxPriceGold = 100_000_000_000;
    private static readonly HashSet<string> TradableRarities = ["Magic", "Rare", "Legendary", "Unique", "Mythic"];

    private readonly HashSet<string> _itemTypes = catalog.Entries
        .SelectMany(e => e.ItemType is { } type ? [.. e.ItemTypes, type] : e.ItemTypes)
        .ToHashSet(StringComparer.Ordinal);

    public int Season => catalog.Season;

    /// <summary>Whether the item type is a kind of goods (runes, gems, elixirs, materials), listed by name and quantity.</summary>
    public bool IsGoods(string itemType) => catalog.Goods.Any(g => g.Kind == itemType);

    /// <summary>The class's bit in <see cref="ClassMask"/>, or 0 for an unknown class.</summary>
    public int ClassBit(string className) => catalog.Classes.ToList().IndexOf(className) is var i and >= 0 ? 1 << i : 0;

    /// <summary>
    /// The classes that can use the item, one bit per class in the catalog's order: those that can equip its type,
    /// narrowed by a class-specific aspect or unique power and by class-specific affixes.
    /// </summary>
    public int ClassMask(NewListing listing)
    {
        var mask = catalog.ItemTypeClasses.TryGetValue(listing.ItemType, out var equip) ? Mask(equip) : (1 << catalog.Classes.Count) - 1;
        var restricted = listing.Affixes.Select(a => catalog.FindById(a.Id))
            .Append(listing.AspectId is { } id ? catalog.FindById(id) : null);
        foreach (var entry in restricted)
            if (entry is { Classes.Count: > 0 } && entry.Classes.Count < catalog.Classes.Count)
                mask &= Mask(entry.Classes);
        return mask;
    }

    private int Mask(IEnumerable<string> classes) => classes.Aggregate(0, (mask, c) => mask | ClassBit(c));

    /// <summary>Everything wrong with the listing, as messages for the seller; empty when it can be stored.</summary>
    // shortcut: account-bound items are refused by the app (ItemTooltip.AccountBound, TooltipListing.From) but the server
    // can't tell from a listing; check it here if listings ever carry the tooltip's binding line.
    public IReadOnlyList<string> Check(NewListing listing)
    {
        var errors = new List<string>();
        CheckRealm(listing.Realm, errors);
        if (catalog.Goods.Any(g => g.Kind == listing.ItemType))
        {
            CheckGoods(listing, errors);
            return errors;
        }
        if (listing.Quantity != 1)
            errors.Add("Gear is listed one item at a time.");
        if (!_itemTypes.Contains(listing.ItemType))
            errors.Add($"Unknown item type \"{listing.ItemType}\".");
        if (!TradableRarities.Contains(listing.Rarity))
            errors.Add($"{listing.Rarity} items can't be listed.");
        if (string.IsNullOrWhiteSpace(listing.Name) || listing.Name.Length > 100)
            errors.Add("The item name must be 1-100 characters.");
        if (listing.ItemPower is < 1 or > 1000)
            errors.Add("Item power must be between 1 and 1000.");
        if (listing.PriceGold is < 1 or > MaxPriceGold)
            errors.Add($"The price must be between 1 and {MaxPriceGold:N0} gold.");

        if (listing.Affixes.Count > MaxAffixLines)
            errors.Add($"An item has at most {MaxAffixLines} affix lines.");
        foreach (var affix in listing.Affixes)
        {
            var entry = catalog.FindById(affix.Id);
            var expected = affix.Slot switch { AffixSlot.Implicit => AffixKind.Implicit, AffixSlot.Tempered => AffixKind.Tempered, _ => AffixKind.Affix };
            if (entry is null || entry.Kind != expected)
                errors.Add($"Affix {affix.Id} isn't a known {affix.Slot.ToString().ToLowerInvariant()} affix in season {catalog.Season}.");
            else if (entry.ItemTypes.Count > 0 && !entry.ItemTypes.Contains(listing.ItemType))
                errors.Add($"\"{entry.Lines[0]}\" doesn't roll on {listing.ItemType}.");
            if (!double.IsFinite(affix.Value) || Math.Abs(affix.Value) > 10_000_000)
                errors.Add($"Affix {affix.Id} has an impossible value.");
        }
        if (listing.Affixes.GroupBy(a => (a.Id, a.Slot)).Any(g => g.Count() > 1))
            errors.Add("The same affix is listed twice.");

        if (listing.AspectId is { } aspectId)
        {
            var power = catalog.FindById(aspectId);
            if (power is null || power.Kind is not (AffixKind.Aspect or AffixKind.Unique or AffixKind.Mythic))
                errors.Add($"Power {aspectId} isn't a known aspect or unique power in season {catalog.Season}.");
            else if (power.ItemType is { } uniqueType && uniqueType != listing.ItemType)
                errors.Add($"{power.Name} is a {uniqueType}, not a {listing.ItemType}.");
        }
        return errors;
    }

    public const int MaxQuantity = 999;
    public const string GoodsRarity = "Common"; // goods have no rarity of their own; listings carry this one

    /// <summary>A rune, gem, elixir or material: a known name of that kind, a quantity, a price, and nothing rolled.</summary>
    private void CheckGoods(NewListing listing, List<string> errors)
    {
        if (!catalog.Goods.Any(g => g.Kind == listing.ItemType && g.Name == listing.Name))
            errors.Add($"\"{listing.Name}\" isn't a known {listing.ItemType.ToLowerInvariant()} in season {catalog.Season}.");
        if (listing.Quantity is < 1 or > MaxQuantity)
            errors.Add($"The quantity must be between 1 and {MaxQuantity}.");
        if (listing.PriceGold is not (>= 1 and <= MaxPriceGold))
            errors.Add($"Goods need a price between 1 and {MaxPriceGold:N0} gold, for them all."); // so a price each can be shown
        if (listing.ItemPower != 0 || listing.Ancestral || listing.Rarity != GoodsRarity)
            errors.Add($"A {listing.ItemType.ToLowerInvariant()} has no item power, isn't ancestral, and is listed as {GoodsRarity}.");
        if (listing.Affixes.Count > 0 || listing.AspectId is not null)
            errors.Add($"A {listing.ItemType.ToLowerInvariant()} has no affixes or aspect to list.");
    }

    internal void CheckRealm(Realm realm, List<string> errors)
    {
        if (realm.Kind == RealmKind.Eternal && realm.Season != 0)
            errors.Add("Eternal realm listings have no season (use 0).");
        // Seasonal items are checked against this season's data; older seasons' items have moved to the eternal realm.
        if (realm.Kind == RealmKind.Seasonal && realm.Season != catalog.Season)
            errors.Add($"Seasonal listings are for season {catalog.Season}.");
    }
}
