using Deckard.Core.Trade;
using Deckard.Trade.Api;
using static Deckard.Trade.Tests.Fixtures;

namespace Deckard.Trade.Tests;

public class ListingRulesTests
{
    private static readonly ListingRules Rules = new(Catalog());

    [Fact]
    public void AcceptsAValidEternalListing()
    {
        Assert.Empty(Rules.Check(Ring()));
    }

    [Fact]
    public void AcceptsTheCurrentSeasonOnly()
    {
        Assert.Empty(Rules.Check(Ring() with { Realm = Realm.Seasonal(15, hardcore: true) }));
        Assert.NotEmpty(Rules.Check(Ring() with { Realm = Realm.Seasonal(14) }));
        Assert.NotEmpty(Rules.Check(Ring() with { Realm = new Realm(RealmKind.Eternal, false, 15) }));
    }

    [Fact]
    public void RejectsAffixesTheItemCantHave()
    {
        var onBoots = Ring() with { ItemType = "Gloves" }; // attack speed and the ring implicit don't roll on gloves
        var errors = Rules.Check(onBoots);

        Assert.Contains(errors, e => e.Contains("Attack Speed"));
        Assert.Contains(errors, e => e.Contains("Resistance"));
    }

    [Fact]
    public void RejectsUnknownOrMisplacedIds()
    {
        Assert.NotEmpty(Rules.Check(Ring() with { Affixes = [new ListedAffix(999, 5, false, AffixSlot.Affix)] }));
        Assert.NotEmpty(Rules.Check(Ring() with { Affixes = [new ListedAffix(TemperedCrit, 5, false, AffixSlot.Affix)] })); // a temper sent as an affix
        Assert.NotEmpty(Rules.Check(Ring() with { AspectId = CritChance }));
    }

    [Fact]
    public void RejectsAUniqueOnTheWrongItemType()
    {
        Assert.Empty(Rules.Check(Ring() with { Rarity = "Unique", AspectId = StarlessSkies }));
        Assert.NotEmpty(Rules.Check(Ring() with { ItemType = "Gloves", Affixes = [], Rarity = "Unique", AspectId = StarlessSkies }));
    }

    [Fact]
    public void WorksOutWhichClassesCanUseTheItem()
    {
        int Bits(params string[] classes) => classes.Aggregate(0, (mask, c) => mask | Rules.ClassBit(c));

        Assert.Equal(Bits("Sorcerer", "Necromancer", "Rogue"), Rules.ClassMask(Ring()));                    // anyone wears a ring
        Assert.Equal(Bits("Sorcerer"), Rules.ClassMask(Ring() with { AspectId = SorcererAspect }));         // a Sorcerer aspect
        Assert.Equal(Bits("Sorcerer", "Necromancer"), Rules.ClassMask(Ring() with { ItemType = "Wand", Affixes = [] })); // wands
        Assert.Equal(0, Rules.ClassBit("Paladin"));                                                         // not in this catalog
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(ListingRules.MaxPriceGold + 1)]
    public void RejectsImpossiblePrices(long price)
    {
        Assert.NotEmpty(Rules.Check(Ring(price: price)));
    }

    [Fact]
    public void RejectsDuplicateAndImpossibleValues()
    {
        Assert.NotEmpty(Rules.Check(Ring() with { Affixes = [new ListedAffix(CritChance, 5, false, AffixSlot.Affix), new ListedAffix(CritChance, 6, false, AffixSlot.Affix)] }));
        Assert.NotEmpty(Rules.Check(Ring(crit: double.NaN)));
    }
}
