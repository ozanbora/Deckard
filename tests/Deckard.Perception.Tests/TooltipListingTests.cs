using Deckard.Core.Capture;
using Deckard.Core.Items;
using Deckard.Core.Trade;
using Deckard.Perception.Tooltips;
using Deckard.Perception.Trade;

namespace Deckard.Perception.Tests;

public class TooltipListingTests
{
    private static AffixEntry Entry(AffixKind kind, long id, string line, params string[] itemTypes) =>
        new() { Kind = kind, Keys = [$"key{id}"], Ids = [id], Lines = [line], ItemTypes = itemTypes };

    private static readonly AffixEntry Crit = Entry(AffixKind.Affix, 3, "+#% Critical Strike Chance", "Ring");
    private static readonly AffixEntry FireAffix = Entry(AffixKind.Affix, 10, "+#% Fire Resistance", "Chest Armor");
    private static readonly AffixEntry FireImplicit = Entry(AffixKind.Implicit, 11, "+#% Fire Resistance", "Ring");
    private static readonly AffixEntry Virtuous = new() { Kind = AffixKind.Aspect, Keys = ["a"], Ids = [6], Lines = ["x#% damage"], Name = "Virtuous Aspect" };

    private static TooltipLine Line(string text, params (AffixEntry Entry, double Value)[] matches) =>
        new(text, new PixelRect(0, 0, 1, 1), [.. matches.Select(m => new AffixMatch(m.Entry, 0, [m.Value]))]);

    private static ItemTooltip Ring() => new()
    {
        Bounds = new PixelRect(0, 0, 1, 1),
        Name = "Virtuous Axolotl's Smile",
        ItemType = "Ring",
        Rarity = "Legendary",
        ItemPower = 800,
        Ancestral = true,
        NamedPower = Virtuous,
        Lines =
        [
            Line("+25.0% Fire Resistance", (FireAffix, 25), (FireImplicit, 25)), // the chest affix can't be on a ring
            Line("+5.5% Critical Strike Chance", (Crit, 5.5)),
            Line("Requires Level 60"),
        ],
    };

    [Fact]
    public void ListsTheAffixesThatRollOnTheItem()
    {
        var listing = TooltipListing.From(Ring(), Realm.Eternal(), 25_000_000)!;

        Assert.Equal(("Ring", "Legendary", 800, true, 6L), (listing.ItemType, listing.Rarity, listing.ItemPower, listing.Ancestral, listing.AspectId!.Value));
        Assert.Equal(
            [new ListedAffix(11, 25, false, AffixSlot.Implicit), new ListedAffix(3, 5.5, false, AffixSlot.Affix)],
            listing.Affixes);
        Assert.Equal(25_000_000, listing.PriceGold);
    }

    [Fact]
    public void NeedsTypeRarityAndPower()
    {
        Assert.Null(TooltipListing.From(Ring() with { ItemPower = null }, Realm.Eternal(), null));
        Assert.Null(TooltipListing.From(Ring() with { ItemType = null }, Realm.Eternal(), null));
    }

    [Fact]
    public void AGreaterAffixIsListedAndPriceCheckedAsGreater()
    {
        var ring = Ring();
        ring = ring with { Lines = [ring.Lines[0], ring.Lines[1] with { Greater = true }, ring.Lines[2]] };
        var listing = TooltipListing.From(ring, Realm.Eternal(), null)!;

        Assert.True(listing.Affixes.Single(a => a.Id == 3).Greater);
        var similar = TooltipListing.Similar(listing);
        Assert.Equal(("Ring", "Legendary", 6L), (similar.ItemType, similar.Rarity, similar.AspectId!.Value));
        Assert.Equal([new AffixFilter(3, Greater: true)], similar.Affixes); // affixes only, not the implicit
    }
}
