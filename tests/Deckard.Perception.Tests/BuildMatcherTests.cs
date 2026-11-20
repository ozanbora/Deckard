using Deckard.Core.Builds;
using Deckard.Perception.Builds;

namespace Deckard.Perception.Tests;

public class BuildMatcherTests
{
    // The ring tooltip (TooltipParserTests) has Maximum Life (1), Attack Speed (2), Crit Chance (3), Poison Resistance (4)
    // and the Virtuous aspect (6).
    private static BuildVariant Plan(params PlannedItem[] items) =>
        new("Endgame", 6, 70, items.ToDictionary(i => i.Slot), [], [], "", []);

    private static PlannedItem Ring(int slot, string key, long[] affixes, long[] aspects) =>
        new(slot, key, [.. affixes.Select(id => new PlannedAffix(id, false))], [], [.. aspects.Select(id => new PlannedAffix(id, false))]);

    [Fact]
    public void ScoresPlannedAffixesAndAspect()
    {
        // Wants Maximum Life and Thorns plus the Virtuous aspect; the item has life and the aspect, not Thorns.
        var plan = Plan(Ring(16, "Ring_Legendary_Generic_008", [1, 9, 999], [6]));

        var result = BuildMatcher.Compare(TooltipParserTests.ParseRing(), plan, TooltipParserTests.LoadCatalog());

        Assert.NotNull(result);
        Assert.Equal(0.75, result.Score); // (1 affix + aspect x2) / (2 known affixes + aspect x2); unknown id 999 ignored
        Assert.Equal(["S04_Life"], result.Found.Select(e => e.Keys[0]));
        Assert.Equal(["S04_Thorns"], result.Missing.Select(e => e.Keys[0]));
        Assert.True(result.AspectMatches);
    }

    [Fact]
    public void ScoresAListedItemLikeAReadOne()
    {
        // The same ring as a trade listing: life and attack speed by id, and the Virtuous aspect.
        var plan = Plan(Ring(16, "Ring_Legendary_Generic_008", [1, 9, 999], [6]));
        var catalog = TooltipParserTests.LoadCatalog();

        var listed = BuildMatcher.CompareListed("Ring", "Virtuous Ring", 6, [1, 2], plan, catalog);
        var read = BuildMatcher.Compare(TooltipParserTests.ParseRing(), plan, catalog);

        Assert.Equal(read?.Score, listed?.Score);
        Assert.Equal(["S04_Thorns"], listed?.Missing.Select(e => e.Keys[0]));
        Assert.Null(BuildMatcher.CompareListed("Amulet", "Some Amulet", null, [1], plan, catalog)); // nothing planned
    }

    [Fact]
    public void PicksTheBestFittingSlot()
    {
        // Two ring slots: a unique the item isn't, and a legendary it fits.
        var plan = Plan(
            Ring(16, "Ring_Unique_Generic_001", [1], []),
            Ring(17, "Ring_Legendary_Generic_008", [1, 2], [6]));

        var result = BuildMatcher.Compare(TooltipParserTests.ParseRing(), plan, TooltipParserTests.LoadCatalog());

        Assert.Equal(17, result?.Planned.Slot);
        Assert.Equal(1.0, result?.Score);
    }

    [Fact]
    public void ADifferentUniqueScoresZero()
    {
        var plan = Plan(Ring(16, "Ring_Unique_Generic_001", [1, 2], []));

        var result = BuildMatcher.Compare(TooltipParserTests.ParseRing(), plan, TooltipParserTests.LoadCatalog());

        Assert.Equal(0, result?.Score);
        Assert.False(result?.SameUnique);
        Assert.Equal("Stone of Jordan", result?.PlannedName);
    }

    [Fact]
    public void NothingPlannedForThisItemType()
    {
        var plan = Plan(new PlannedItem(4, "Helm_Unique_Generic_005", [], [], []));

        Assert.Null(BuildMatcher.Compare(TooltipParserTests.ParseRing(), plan, TooltipParserTests.LoadCatalog()));
    }

    [Theory]
    [InlineData(0, true, ItemVerdict.Keep)]
    [InlineData(1, true, ItemVerdict.Craft)]   // enchant the missing affix
    [InlineData(0, false, ItemVerdict.Craft)]  // imprint the aspect
    [InlineData(1, false, ItemVerdict.Craft)]  // both
    [InlineData(2, true, ItemVerdict.Salvage)] // enchanting fixes only one
    public void ReachesAVerdictFromWhatTheItemLacks(int missing, bool aspect, ItemVerdict expected)
    {
        var catalog = TooltipParserTests.LoadCatalog();
        var life = catalog.FindById(1)!;
        var fit = new ItemComparison(Ring(16, "Ring_Legendary_Generic_008", [], []), "Ring", 0, [life], [.. Enumerable.Repeat(life, missing)], aspect, null);

        Assert.Equal(expected, fit.Verdict?.Verdict);
    }

    [Fact]
    public void SalvagesAnotherUniqueAndKeepsTheBuildsOwn()
    {
        var planned = Ring(16, "Ring_Unique_Generic_001", [], []);
        Assert.Equal(ItemVerdict.Salvage, new ItemComparison(planned, "Stone of Jordan", 0, [], [], null, false).Verdict?.Verdict);
        Assert.Equal(ItemVerdict.Keep, new ItemComparison(planned, "Stone of Jordan", 1, [], [], null, true).Verdict?.Verdict);
        Assert.Null(new ItemComparison(Ring(16, "Ring_Legendary_Generic_008", [], []), "Ring", 0, [], [], null, null).Verdict);
    }
}
