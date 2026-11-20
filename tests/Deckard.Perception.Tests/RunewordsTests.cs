using System.Text;
using Deckard.Core.Builds;
using Deckard.Core.Capture;
using Deckard.Core.Items;
using Deckard.Perception.Builds;
using Deckard.Perception.Tooltips;

namespace Deckard.Perception.Tests;

public class RunewordsTests
{
    // Runes as the season data has them: Rituals gain Offering, Invocations need it; a gem; the guide's items.
    private const string Catalog = """
        {
          "schemaVersion": 8, "game": "d4", "season": 15, "gameBuild": "3.2.1.73552", "locale": "enus", "classes": [], "entries": [],
          "items": {
            "Helm_Legendary_Generic_053": { "name": "Runic Skullcap", "type": "Helm" },
            "Chest_Legendary_Generic_053": { "name": "Runic Mail", "type": "Chest Armor" },
            "2HSword_Legendary_Generic_001": { "name": "Greatsword", "type": "Two-Handed Sword", "twoHanded": true },
            "1HAxe_Legendary_Generic_001": { "name": "Bearded Axe", "type": "Axe" },
            "1HFocus_Rare_Generic_002": { "name": "Blood Orb", "type": "Focus" }
          },
          "goods": [
            { "key": "S15_Rune_Condition_Tal", "id": 1, "name": "Tal", "kind": "Rune", "ritual": true, "offering": 3, "lines": ["Deal Damage over Time", "Gains 3 Offering"] },
            { "key": "Rune_Condition_OnEvade", "id": 2, "name": "Cem", "kind": "Rune", "ritual": true, "offering": 75, "lines": ["Cast Evade.", "Gains 75 Offering"] },
            { "key": "S15_Rune_Condition_Ort", "id": 3, "name": "Ort", "kind": "Rune", "ritual": true, "offering": 1, "lines": ["Deal Non-Physical Direct Damage"] },
            { "key": "S15_Rune_Condition_Eth", "id": 8, "name": "Eth", "kind": "Rune", "ritual": true, "offering": 10, "lines": ["Deal Critical Damage"] },
            { "key": "Rune_Effect_CritBuff", "id": 4, "name": "Gar", "kind": "Rune", "ritual": false, "offering": 25, "lines": ["Gain 2% Critical Strike Chance for 5 seconds, up to 10%."] },
            { "key": "Rune_Effect_MobilityBuff", "id": 5, "name": "Qua", "kind": "Rune", "ritual": false, "offering": 50, "lines": ["Gain 10% Movement Speed for 5 seconds, up to 50%."] },
            { "key": "S15_Rune_Effect_Sol", "id": 6, "name": "Sol", "kind": "Rune", "ritual": false, "offering": 350, "lines": ["Gain 15% Damage Reduction for 6 seconds."] },
            { "key": "Gem_Sapphire_03", "id": 7, "name": "Chipped Sapphire", "kind": "Gem", "lines": ["Weapon: x16% Critical Strike Damage", "Armor: +20 Willpower"] },
            { "key": "Gem_Ruby_03", "id": 9, "name": "Chipped Ruby", "kind": "Gem", "lines": ["Weapon: x16% Overpower Damage", "Armor: +4% Maximum Life"] }
          ]
        }
        """;

    private static readonly AffixCatalog Data = AffixCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(Catalog)));

    // A guide like the leveling Warlock's: gems in the helm, Cem + Qua in the chest.
    private static readonly PlannedItem Helm = new(4, "Helm_Legendary_Generic_053", [], [], [], Sockets: ["Gem_Sapphire_03", "Gem_Sapphire_03"]);
    private static readonly PlannedItem Chest = new(5, "Chest_Legendary_Generic_053", [], [], [], Sockets: ["Rune_Condition_OnEvade", "Rune_Effect_MobilityBuff"]);
    private static readonly BuildVariant Guide = new("Leveling", 7, 0, new Dictionary<int, PlannedItem> { [4] = Helm, [5] = Chest }, [], [], "", []);

    private static ItemTooltip Read(string name, string type, int sockets = 0) =>
        new() { Bounds = new PixelRect(0, 0, 1, 1), Name = name, ItemType = type, EmptySockets = sockets, Lines = [] };

    [Theory]
    [InlineData("Tal (4)", "S15_Rune_Condition_Tal", 4)]
    [InlineData("ØRT (2)", "S15_Rune_Condition_Ort", 2)] // the game's font draws O as Ø
    [InlineData("0rt (2)", "S15_Rune_Condition_Ort", 2)]
    [InlineData("Cem", "Rune_Condition_OnEvade", 1)]      // one: no count
    public void LearnsOwnedRunesFromTheirTooltips(string name, string key, int count) =>
        Assert.Equal((key, count), Runewords.Owned(Read(name, "Rune of Ritual"), Data));

    [Theory]
    // As OCR read live ØRT tooltips: the Ø as C, the panel's Toughness above taken into the header, or another rune's name.
    [InlineData("4,809 Crt (2)")]
    [InlineData("Eth")]
    [InlineData("")]
    public void TellsARuneByItsEffectAndOfferingWhenTheNameMisreads(string header)
    {
        var tooltip = Read(header, "Rune of Ritual") with
        {
            RawName = header,
            Lines = [new TooltipLine("Gain: 1 Offering Deal Non-Physical Direct Damage Link with a Rune of Invocation", new PixelRect(0, 0, 1, 1), [])],
        };
        var (rune, count) = Runewords.Identify(tooltip, Data);
        Assert.Equal("Ort", rune?.Name);
        Assert.Equal(header.Contains("(2)", StringComparison.Ordinal) ? 2 : 1, count);
    }

    [Fact]
    public void DescribesWhatsSocketed()
    {
        // The chest as the game shows it with the guide's runeword in; a helm with two sapphires.
        var chest = Read("Dire Finery", "Chest Armor") with { Socketed = ["CemQua (75/50) - Acrobatic Wind Walk Cast Evade. (1 time)"] };
        Assert.Equal(["Rune_Condition_OnEvade", "Rune_Effect_MobilityBuff"], Runewords.SocketedRunes(chest, Data));
        Assert.Equal(["Runeword socketed: Cem + Qua, the guide's ✓"], Runewords.Socketed(chest, Chest, Data));

        var helm = Read("Greathelm", "Helm") with { Socketed = ["20 Willpower", "20 Willpower"] };
        Assert.Empty(Runewords.SocketedRunes(helm, Data));
        Assert.Equal(["Chipped Sapphire ×2 socketed: 20 Willpower each"], Runewords.Socketed(helm, Helm, Data));
        Assert.Equal(["Socketed: 7% Lucky Hit"], Runewords.Socketed(Read("Ring", "Ring") with { Socketed = ["7% Lucky Hit"] }, null, Data)); // no gem gives that
    }

    [Fact]
    public void IgnoresTooltipsThatArentRunes()
    {
        Assert.Null(Runewords.Owned(Read("Tal (4)", "Helm"), Data));
        Assert.Null(Runewords.Owned(Read("Zod (1)", "Rune of Invocation"), Data)); // not in this season's data
    }

    [Fact]
    public void SuggestsTheGuidesRunewordTheyCanMake()
    {
        var owned = new Dictionary<string, int> { ["Rune_Condition_OnEvade"] = 1, ["Rune_Effect_MobilityBuff"] = 1, ["S15_Rune_Condition_Tal"] = 4 };
        var advice = Runewords.Advise(Read("Pyroclastic Warlord Greathelm", "Helm", sockets: 2), Helm, Guide, owned, Data);

        Assert.Equal("Socket Chipped Sapphire ×2 here.", advice[0]);
        // Said with where it goes: the guide wants gems in this helm, the runeword in the chest.
        Assert.Contains("You have both runes for the guide's chest armor (Runic Mail) runeword: Cem + Qua, for your chest armor (Runic Mail), not this item.", advice);
    }

    [Fact]
    public void OtherwisePairsTheirOwnRunesByHowOftenTheyCast()
    {
        // Tal, Ort, Gar and Sol: no guide runeword. Tal + Gar casts every 9 damage-over-time ticks; Ort + Sol needs 350 hits.
        var owned = new Dictionary<string, int> { ["S15_Rune_Condition_Tal"] = 4, ["S15_Rune_Condition_Ort"] = 2, ["Rune_Effect_CritBuff"] = 1, ["S15_Rune_Effect_Sol"] = 2 };
        var advice = Runewords.Advise(Read("Greathelm", "Helm", sockets: 2), Helm, Guide, owned, Data);

        var pairs = advice.Where(l => l.Contains(" + ", StringComparison.Ordinal) && !l.StartsWith("The guide", StringComparison.Ordinal)).ToList();
        Assert.StartsWith("Tal + Gar: Deal Damage over Time gains 3, Gar needs 25, a cast every 9 times.", pairs[0]);
        Assert.DoesNotContain(pairs, l => l.StartsWith("Ort + Sol", StringComparison.Ordinal)); // 350 hits: last, out of the top three
    }

    [Fact]
    public void AsksForTheRunesWhenNoneAreKnown()
    {
        var advice = Runewords.Advise(Read("Greathelm", "Helm", sockets: 2), Helm, Guide, owned: null, Data);
        Assert.Contains("The guide's runewords: Cem + Qua in its chest armor (Runic Mail).", advice);
        Assert.Contains(advice, l => l.StartsWith("Hover your runes once", StringComparison.Ordinal));

        Assert.Empty(Runewords.Advise(Read("Greathelm", "Helm", sockets: 0), Helm, Guide, owned: null, Data)); // no sockets: nothing
    }

    [Theory]
    [InlineData("Bearded Axe of Calamity", "Axe")]
    [InlineData("Night Gift", "Focus")]
    public void AWeaponTheGuideDoesntUseGoesAgainstItsWeapon(string name, string type)
    {
        // The leveling Warlock's guide has a two-handed sword with two rubies and no off-hand; the player wields an axe and a focus.
        var sword = new PlannedItem(7, "2HSword_Legendary_Generic_001", [], [], [], Sockets: ["Gem_Ruby_03", "Gem_Ruby_03"]);
        var guide = Guide with { Items = new Dictionary<int, PlannedItem> { [4] = Helm, [7] = sword } };
        var item = Read(name, type, sockets: 1);

        var fit = BuildMatcher.Compare(item, guide, Data);

        Assert.Equal(7, fit?.Planned.Slot);
        // One socket: one of the guide's two rubies.
        Assert.Equal(["Socket Chipped Ruby here, as the guide does in its two-handed sword (Greatsword)."], Runewords.Advise(item, fit?.Planned, guide, owned: null, Data));
        Assert.Null(BuildMatcher.Compare(Read("Ring", "Ring"), guide, Data)); // not a weapon: nothing planned for it
    }
}
