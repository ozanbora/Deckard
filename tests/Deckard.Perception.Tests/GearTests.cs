using Deckard.Core.Builds;
using Deckard.Perception.Builds;

namespace Deckard.Perception.Tests;

public class GearTests
{
    // Fixture ids (TooltipParserTests): 1 Maximum Life, 2 Attack Speed, 3 Crit Chance, 4 Poison Resistance, 6 the Virtuous
    // aspect, 9 Thorns. Two ring slots: one wants life, attack speed and the aspect; the other crit and thorns.
    private static readonly BuildVariant Plan = new("Endgame", 6, 70, new Dictionary<int, PlannedItem>
    {
        [16] = new(16, "Ring_Legendary_Generic_008", [new(1, false), new(2, false)], [new(3, false)], [new(6, false)]),
        [17] = new(17, "Ring_Legendary_Generic_008", [new(3, false), new(9, false)], [], [new(6, false)]),
    }, [], [], "", []);

    private static WornItem Ring(string name, long[] ids, int minute) => new("Ring", name, "Legendary", ids, DateTimeOffset.UnixEpoch.AddMinutes(minute));

    [Fact]
    public void KeepsAsManyOfATypeAsTheBuildHasSlots()
    {
        var catalog = TooltipParserTests.LoadCatalog();
        var a = Ring("A", [1, 2], 1);
        var b = Ring("B", [3, 9], 2);
        var c = Ring("C", [4], 3);

        var worn = Gear.Remember(Gear.Remember([], a, Plan, catalog), b, Plan, catalog);
        Assert.Equal(["A", "B"], worn.Select(w => w.Name));
        Assert.Equal(["B", "C"], Gear.Remember(worn, c, Plan, catalog).Select(w => w.Name)); // two ring slots: the oldest goes
        Assert.Equal(["B", "A"], Gear.Remember(worn, a with { ReadAt = a.ReadAt.AddHours(1) }, Plan, catalog).Select(w => w.Name)); // read again: replaced
    }

    [Fact]
    public void PutsEachWornItemInTheSlotItFitsBest()
    {
        var catalog = TooltipParserTests.LoadCatalog();
        // Read crit/thorns first, so it isn't simply first come, first served.
        var worn = new[] { Ring("Crit", [3, 9, 6], 1), Ring("Life", [1, 2, 6], 2) };

        var assigned = Gear.Assign(worn, Plan, catalog);

        Assert.Equal("Life", assigned[16].Item.Name);
        Assert.Equal("Crit", assigned[17].Item.Name);
        Assert.Equal(ItemVerdict.Keep, assigned[17].Fit.Verdict?.Verdict);
    }

    [Fact]
    public void ListsWhatsLeftToCraft()
    {
        var catalog = TooltipParserTests.LoadCatalog();
        // Life but no attack speed, no aspect and no tempered crit: imprint, enchant, temper.
        var ring = Ring("Ring", [1], 1);
        var fit = BuildMatcher.CompareWith(ring.Name, ring.Ids, Plan.Items[16], catalog);

        var tasks = Gear.Tasks(ring, fit, catalog).ToList();

        Assert.Equal([CraftKind.Imprint, CraftKind.Enchant, CraftKind.Temper], tasks.Select(t => t.Kind));
        Assert.Equal(["16:imprint:6", "16:enchant:2", "16:temper:3"], tasks.Select(t => t.Key));

        // Nothing the build wants: replace it, and nothing else.
        var junk = Ring("Junk", [4], 1);
        Assert.Equal([CraftKind.Replace], Gear.Tasks(junk, BuildMatcher.CompareWith(junk.Name, junk.Ids, Plan.Items[17], catalog), catalog).Select(t => t.Kind));
    }
}
