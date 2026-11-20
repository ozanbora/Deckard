using System.Text;
using Deckard.Core.Capture;
using Deckard.Core.Items;
using Deckard.Perception.Ocr;
using Deckard.Perception.Tooltips;
using Deckard.Perception.Trade;

namespace Deckard.Perception.Tests;

/// <summary>
/// A 1080p Impact Hammer of Proselytizing as OCR read it (the advanced tooltip, with roll ranges): wrapped lines, 1/l
/// mix-ups, a fixed roll read with its brackets as 1s, a gem behind an icon, and an account-bound, scrolling footer.
/// </summary>
public class WrappedTooltipTests
{
    private const string Catalog = """
        {
          "schemaVersion": 8, "game": "d4", "season": 15, "gameBuild": "3.2.1.73552", "locale": "enus", "classes": [],
          "entries": [
            { "kind": "affix", "keys": ["Strength"], "ids": [1], "lines": ["+# Strength"] },
            { "kind": "affix", "keys": ["Life"], "ids": [2], "lines": ["+# Maximum Life"] },
            { "kind": "affix", "keys": ["LifeOnKill"], "ids": [3], "lines": ["+# Life On Kill"] },
            { "kind": "affix", "keys": ["CritDamageMult"], "ids": [4], "lines": ["x#% Critical Strike Damage Multiplier"] },
            { "kind": "tempered", "keys": ["Tempered_CritDamage"], "ids": [5], "lines": ["+#% Critical Strike Damage"] },
            { "kind": "affix", "keys": ["X2_CoreStats_All"], "ids": [6], "lines": ["# All Stats"], "fixed": true },
            { "kind": "aspect", "keys": ["legendary_paladin_proselytizing"], "ids": [7], "name": "Aspect of Proselytizing", "suffix": "of Proselytizing",
              "lines": ["Judicator Skills have a #% chance to chain to a Close enemy for #%[x] of their damage. If no enemy is within range, damage the initial target instead."] },
            { "kind": "affix", "keys": ["PoisonDamageMult"], "ids": [8], "lines": ["x#% Poison Damage Multiplier"] },
            { "kind": "affix", "keys": ["ResourceCostReduction"], "ids": [9], "lines": ["#% Resource Cost Reduction"] },
            { "kind": "affix", "keys": ["CooldownReduction"], "ids": [10], "lines": ["#% Cooldown Reduction"] },
            { "kind": "affix", "keys": ["Armor"], "ids": [11], "lines": ["+# Armor"] }
          ],
          "baseNames": { "Dagger": ["Dagger"] }
        }
        """;

    private static readonly string[] Ocr =
    [
        "IMPACT HAMMER", "OF PROSELYTIZING",
        "Ancestral Legendary Mace",
        "800 Item Power",
        "25 • +25)Qua1ity",
        "1,304 Damage Per Second",
        "[988 - 1,384] Damage per Hit",
        "1. IO Attacks per Second (Fast)",
        "+125 Strength",
        "+1,110 Maximum Life",
        "+251 LifeonKi11+[196-266]",
        "+21% Critical Strike Damage",
        "Multiplier [13 - 251%",
        "X +100 All Stats +11001",
        "* Judicator Skills have a 40% chance to",
        "chain to a Close enemy for 145%[",
        "of their damage. If no",
        "enemy is within range, damage the",
        "initial target instead.",
        "@x22% Poison Damage Multiplier",
        "Requires Level 60",
        "Account Bound",
        "Scroll Down",
    ];

    private static ItemTooltip Parse(string[]? ocr = null)
    {
        var catalog = AffixCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(Catalog)));
        var lines = (ocr ?? Ocr).Select((text, i) => new TextLine(text, new PixelRect(10, 20 + i * 30, 340, 24))).ToList();
        return new TooltipParser(catalog).Parse(lines, new PixelRect(0, 0, 363, 718));
    }

    [Fact]
    public void ReadsGluedAffixesAndParagraphsOfUnknownText()
    {
        // Leoric's Crown at 3440x1440: a range whose start OCR lost, a socket's effect wrapped over lines with an affix
        // printed straight after its last sentence.
        var t = Parse([
            "LEORIC'S CROWN", "Unique Helm", "174 Item Power",
            "+125 Strength - 9]",
            "Your kills absorb a soul for 10", "seconds. Each soul increases your", "damage by 1.5%[x]. This effect does not", "refresh.+11 All Stats",
            "Killing an Elite Pack increases", "Monster Power by 1 for 15 seconds.+2,698", "Maximum Life",
        ]);

        Assert.Equal(["Strength", "?", "X2_CoreStats_All", "?", "Life"], t.Lines.Select(l => l.Matches.FirstOrDefault()?.Entry.Keys[0] ?? "?"));
        Assert.Equal("Your kills absorb a soul for 10 seconds. Each soul increases your damage by 1.5%[x]. This effect does not refresh.", t.Lines[1].Text);
        Assert.Equal([2698.0], t.Lines[4].Matches[0].Values);
    }

    [Fact]
    public void AScrolledTooltipAddsItsLowerLines()
    {
        var top = Parse(["LEORIC'S CROWN", "Unique Helm", "174 Item Power", "+125 Strength", "+251 Life On Kill", "Scroll Down"]);
        var scrolled = Parse(["LEORIC'S CROWN", "Unique Helm", "174 Item Power", "+251 Life On Kill", "+1,110 Maximum Life",
            "Empty Socket", "Requires Level 60", "Account Bound"]);

        var merged = ItemTooltip.MergeScrolled(top, scrolled);

        Assert.True(top.Truncated);
        Assert.NotNull(merged);
        Assert.Equal(["Strength", "LifeOnKill", "Life"], merged.Lines.Select(l => l.Matches[0].Entry.Keys[0]));
        Assert.Equal(1, merged.EmptySockets);
        Assert.True(merged.AccountBound);
        Assert.False(merged.Truncated);
        Assert.Null(ItemTooltip.MergeScrolled(merged, scrolled)); // whole now: a new read replaces it
    }

    [Fact]
    public void AScrolledTooltipWithoutItsHeaderStillAdds()
    {
        // Scrolled to the bottom, the name and item power are gone; two affixes on screen in both reads tie them.
        var top = Parse(["LEORIC'S CROWN", "Unique Helm", "174 Item Power", "+125 Strength", "+251 Life On Kill", "Scroll Down"]);
        // OCR reads a line again a little differently ("Life on Kill"): the same affix and value, not a new line.
        var bottom = Parse(["+125 Strength", "+251 Life on Kill [196 - 266]", "+1,110 Maximum Life", "Requires Level 60", "Account Bound", "Unique Equipped"]);
        var other = Parse(["+90 Strength", "+251 Life On Kill", "Requires Level 60"]);

        var merged = ItemTooltip.MergeScrolled(top, bottom);

        Assert.NotNull(merged);
        Assert.Equal("Leoric's Crown", merged.Name);
        Assert.Equal(["Strength", "LifeOnKill", "Life"], merged.Lines.Select(l => l.Matches[0].Entry.Keys[0])); // no repeats
        Assert.True(merged.AccountBound);
        Assert.Null(ItemTooltip.MergeScrolled(top, other)); // only one affix in common: another item
    }

    [Fact]
    public void ReadsACommonItem()
    {
        // A white story dagger at 3440x1440: the type line is the bare type, the body flavour text.
        var t = Parse([
            "NEYRELLE'S", "DEFIANCE", "Dagger", "23 Item Power", "22 Damage Per Second ( -3)",
            "A tiny message from Vhenard to Neyrelle is", "carved near the pommel: \"The search for",
            "Lord of Hatred Item", "Sell Value: 39", "Durability: 100/100",
        ]);

        Assert.Equal("Neyrelle's Defiance", t.Name);
        Assert.Equal("Common", t.Rarity);
        Assert.Equal("Dagger", t.ItemType);
        Assert.Equal(23, t.ItemPower);
        Assert.Empty(t.Lines);
    }

    [Fact]
    public void ARareNameReadWithAZeroForAnO()
    {
        var t = Parse(["DIRE T0RMENT", "Rare Gloves", "800 Item Power", "Requires Level 60"]);

        Assert.Equal("Dire Torment", t.Name);
    }

    [Fact]
    public void ANameplateAboveTheTooltipIsNoPartOfTheName()
    {
        var catalog = AffixCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(Catalog)));
        TextLine[] lines =
        [
            new("Ina", new PixelRect(10, 5, 40, 16)), // an NPC's nameplate, in the world above the tooltip
            new("DIRE TORMENT", new PixelRect(10, 30, 260, 28)),
            new("Rare Gloves", new PixelRect(10, 64, 160, 22)),
            new("800 Item Power", new PixelRect(10, 92, 160, 22)),
        ];

        Assert.Equal("Dire Torment", new TooltipParser(catalog).Parse(lines, new PixelRect(0, 0, 363, 718)).Name);
    }

    [Fact]
    public void ARangeWrappedOntoItsOwnLineIsNoAffix()
    {
        // Rare gloves at 3440x1440: each range's end wrapped onto a line of its own, its "]" read as a 1.
        var t = Parse([
            "PINCER TALENT", "Rare Gloves", "800 Item Power", "638 Armor (-2.4% Toughness)", // the base armor: no affix
            "5.8% Resource Cost Reduction [4.0 -", "6.01%",
            "6.5% Cooldown Reduction [5.0 -", "8.01%",
            "Requires Level 60",
        ]);

        Assert.Equal(["ResourceCostReduction", "CooldownReduction"], t.Lines.Select(l => l.Matches.FirstOrDefault()?.Entry.Keys[0] ?? l.Text));
    }

    [Fact]
    public void ReadsEveryLineOfAWrappedMisreadTooltip()
    {
        var t = Parse();

        Assert.All(t.Lines, l => Assert.True(l.IsMatched, $"unmatched: {l.Text}"));
        Assert.Equal(
            ["Strength", "Life", "LifeOnKill", "CritDamageMult", "X2_CoreStats_All", "legendary_paladin_proselytizing", "PoisonDamageMult"],
            t.Lines.Select(l => l.Matches[0].Entry.Keys[0]));
        Assert.Equal([21.0], t.Lines[3].Matches[0].Values);   // the multiplier, not the shorter tempered affix on its first line
        Assert.Equal([40.0, 145.0], t.Lines[5].Matches[0].Values); // the aspect, across five lines
        Assert.Equal(1.10, t.AttacksPerSecond);                 // "1. IO"
        Assert.Equal(60, t.RequiredLevel);
    }

    [Fact]
    public void AnAccountBoundItemCantBeListed()
    {
        var t = Parse();

        Assert.True(t.AccountBound);
        Assert.True(t.Truncated);
        Assert.Null(TooltipListing.From(t, Deckard.Core.Trade.Realm.Eternal(), 1_000_000));
    }

    [Theory]
    [InlineData("LifeonKi11", "LifeonKill")]
    [InlineData("A11Stats", "AllStats")]
    [InlineData("Qua1ity", "Quality")]
    [InlineData("+1,110 Maximum Life", "+1,110 Maximum Life")] // numbers stay
    [InlineData("x15% Damage", "x15% Damage")]                 // the multiplier sign isn't a letter here
    public void ReadsOneAsLInsideWords(string ocr, string text) => Assert.Equal(text, AffixTemplate.FixOcrLetters(ocr));
}
