using Deckard.Core.Builds;
using Deckard.Perception.Builds;

namespace Deckard.Perception.Tests;

public class StashSortTests
{
    // The parsed ring (TooltipParserTests): an ancestral legendary with life (1), attack speed (2), crit (3), poison
    // resistance (4) and the Virtuous aspect (6).
    private static BuildVariant Build(params long[] affixes) => new("Endgame", 6, 70, new Dictionary<int, PlannedItem>
    {
        [16] = new(16, "Ring_Legendary_Generic_008", [.. affixes.Select(id => new PlannedAffix(id, false))], [], [new(6, false)]),
    }, [], [], "", []);

    [Fact]
    public void KeepsForTheBuildItFitsAndPrefersKeepingToCrafting()
    {
        var catalog = TooltipParserTests.LoadCatalog();
        var ring = TooltipParserTests.ParseRing();

        // One build wants an affix it lacks (craft), the other has everything it has (keep).
        var verdict = StashSort.Judge(ring, [("Thorns build", Build(1, 9)), ("Speed build", Build(1, 2))], catalog);

        Assert.Equal(StashCall.Keep, verdict.Call);
        Assert.Equal("Speed build", verdict.Build);
        Assert.Equal(StashCall.Craft, StashSort.Judge(ring, [("Thorns build", Build(1, 9))], catalog).Call);
    }

    [Fact]
    public void SellsWhatNoBuildWantsButCanTradeAndSalvagesTheRest()
    {
        var catalog = TooltipParserTests.LoadCatalog();
        var ring = TooltipParserTests.ParseRing() with { AccountBound = false }; // the recorded ring is account bound

        Assert.Equal(StashCall.Sell, StashSort.Judge(ring, [], catalog).Call); // ancestral and tradeable
        Assert.Equal(StashCall.Salvage, StashSort.Judge(ring with { AccountBound = true }, [], catalog).Call);
        Assert.Equal(StashCall.Salvage, StashSort.Judge(ring with { Ancestral = false, Rarity = "Legendary" }, [], catalog).Call);
    }
}
