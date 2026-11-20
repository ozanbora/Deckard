using System.Text;
using Deckard.Core.Capture;
using Deckard.Core.Items;
using Deckard.Perception.Ocr;
using Deckard.Perception.Tooltips;

namespace Deckard.Perception.Tests;

public class TooltipParserTests
{
    // A few Season 15 templates, enough for the tooltip below.
    private const string Catalog = """
        {
          "schemaVersion": 8, "game": "d4", "season": 15, "gameBuild": "3.2.1.73552", "locale": "enus", "classes": [],
          "entries": [
            { "kind": "affix", "keys": ["S04_Life"], "ids": [1], "lines": ["+# Maximum Life"], "prefix": "Vigorous", "suffix": "of Vigor" },
            { "kind": "affix", "keys": ["S04_AttackSpeed"], "ids": [2], "lines": ["+#% Attack Speed"], "prefix": "Keen", "suffix": "of Keenness" },
            { "kind": "affix", "keys": ["S04_CritChance"], "ids": [3], "lines": ["+#% Critical Strike Chance"] },
            { "kind": "affix", "keys": ["S04_Resistance_Single_Poison"], "ids": [4], "lines": ["+# Poison Resistance"] },
            { "kind": "implicit", "keys": ["Resistance_Jewelry_All"], "ids": [5], "lines": ["# All Resist"] },
            { "kind": "aspect", "keys": ["legendary_paladin_x"], "ids": [6], "name": "Virtuous Aspect", "prefix": "Virtuous",
              "lines": ["Using a Valor skill increases your damage by x#% for 7 seconds."] },
            { "kind": "aspect", "keys": ["legendary_generic_y"], "ids": [7], "name": "Aspect of the Indomitable", "suffix": "of the Indomitable",
              "lines": ["Gain Armor and Impairment Reduction equal to #% of your Block Chance."] },
            { "kind": "mythic", "keys": ["2HStaff_Unique_Generic_100"], "ids": [8], "name": "Ahavarion, Spear of Lycander", "itemType": "Staff",
              "lines": ["Gain a random Shrine effect for # seconds after killing an Elite enemy."] },
            { "kind": "affix", "keys": ["S04_Thorns"], "ids": [9], "lines": ["+# Thorns"] }
          ],
          "baseNames": {
            "Ring": ["Axolotl's Smile", "Band", "Blood-staunch Hoop"],
            "Chest Armor": ["Exceptional Armor", "Sturdy Chest Armor"]
          },
          "items": {
            "Ring_Legendary_Generic_008": { "name": "Axolotl's Smile", "type": "Ring" },
            "Ring_Unique_Generic_001": { "name": "Stone of Jordan", "type": "Ring" },
            "Helm_Unique_Generic_005": { "name": "Leoric's Crown", "type": "Helm" }
          }
        }
        """;

    // Windows OCR output (after grayscale preprocessing) for a captured ring tooltip, including its noise:
    // the "EQUIPPED" banner, icon glyphs as separate lines, a misread bracket and a wrapped aspect paragraph.
    private static readonly string[] RingOcr =
    [
        "EQUIPPED",
        "VIRTUOUS",
        "AXOLOTL'S SMILE *",
        "Ancestral Legendary Ring",
        "800 Item Power",
        "25 (e: +25) Quality",
        "139 All Resist",
        "+895 Maximum Life [541 - 740]",
        "*",
        "o +9.8% Attack speed [5.0 - 8.01%",
        "+3.7% Critical strike Chance [2.5 - 3.01%",
        "+2,698",
        "Poison Resistance",
        "Imprinted: Using a Valor skill increases",
        "your damage by 55.0%[x] [40.0 - 60.0]%",
        "for 7 seconds.",
        "BacQue (50/300) - Selachian Fortress",
        "Requires Level 60",
        "Account Bound",
        "Sell Value: 33,288",
    ];

    internal static AffixCatalog LoadCatalog() => AffixCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(Catalog)));

    // Names as Windows OCR read them from Season 15 captures.
    [Theory]
    [InlineData("Irtuous Axolotl's Sm1le", "Legendary", "Ring", "Virtuous Axolotl's Smile", "Axolotl's Smile")]
    [InlineData("Exceptional Art110r Of The Indotiiitable", "Legendary", "Chest Armor", "Exceptional Armor of the Indomitable", "Exceptional Armor")]
    public void Resolves_LegendaryNamesFromAspectAndBase(string ocr, string rarity, string type, string expected, string baseName)
    {
        var resolved = new ItemNameResolver(LoadCatalog()).Resolve(ocr, rarity, type);

        Assert.NotNull(resolved);
        Assert.Equal(expected, resolved.Name);
        Assert.Equal(baseName, resolved.BaseName);
        Assert.Equal(AffixKind.Aspect, resolved.Power?.Kind);
    }

    [Fact]
    public void Resolves_UniqueNames()
    {
        var resolved = new ItemNameResolver(LoadCatalog()).Resolve("AHAVARI0N, SPEAR 0F LYCANDER", "Mythic", "Staff");

        Assert.Equal("Ahavarion, Spear of Lycander", resolved?.Name);
        Assert.Equal(AffixKind.Mythic, resolved?.Power?.Kind);
    }

    [Fact]
    public void KeepsNameWhenNothingIsClose()
    {
        Assert.Null(new ItemNameResolver(LoadCatalog()).Resolve("Grim Coil", "Legendary", "Ring"));
    }

    [Fact]
    public void LeavesRareNamesAlone()
    {
        // Rare names are random word pairs; "Band" is a ring base but must not pull a rare toward a legendary name.
        Assert.Null(new ItemNameResolver(LoadCatalog()).Resolve("Doom Band", "Rare", "Ring"));
    }

    private static AffixEntry Entry(AffixCatalog catalog, string key) => catalog.Entries.First(e => e.Keys[0] == key);

    [Fact]
    public void Resolves_MagicNamesFromTheAffixesRead()
    {
        var catalog = LoadCatalog();
        var body = new[] { Entry(catalog, "S04_Life"), Entry(catalog, "S04_AttackSpeed") };

        var resolved = new ItemNameResolver(catalog).Resolve("V1GOROUS BAND OF KEENNES", "Magic", "Ring", body);

        Assert.Equal("Vigorous Band of Keenness", resolved?.Name);
        Assert.Equal("Band", resolved?.BaseName);
    }

    [Fact]
    public void AspectReadInBodyRescuesAPoorlyReadLegendaryName()
    {
        var catalog = LoadCatalog();
        var aspect = Entry(catalog, "legendary_generic_y");

        var resolved = new ItemNameResolver(catalog).Resolve("Exceptionl Arrnor 0f tlhe Indmitble", "Legendary", "Chest Armor", [aspect]);

        Assert.Equal("Exceptional Armor of the Indomitable", resolved?.Name);
        Assert.Same(aspect, resolved?.Power);
    }

    [Fact]
    public void NameAndBodyAgreeOnTheAspect()
    {
        var tooltip = ParseRing();

        Assert.Equal("Virtuous Aspect", tooltip.NamedPower?.Name);
        Assert.True(tooltip.NameMatchesPower);
    }

    // OCR of a stash sword compared against the equipped weapon (older-season capture).
    [Fact]
    public void ReadsComparisonWeaponTooltip()
    {
        string[] ocr =
        [
            "EDGEMASTER'S", "FLAMMARD", "Ancestral Legendary Sword", "540+25 Item Power",
            "231 Damage Per Second (-2146)", "k• [168 - 252] Damage perHit", "1.10 Attacks per Second (Fast)",
            "+895 Maximum Life [541 - 740] (+12%)", "+9.8% Attack Speed [5.0 - 8.0]% (", "+9.8%)", "[20.0 -",
            "Empty Socket", "Properties lost when equipped:", "+100 All Stats", "Scroll Down", "Take", "Hide Comparison",
        ];
        var lines = ocr.Select((text, i) => new TextLine(text, new PixelRect(10, 20 + i * 30, 400, 24))).ToList();

        var t = new TooltipParser(LoadCatalog()).Parse(lines, new PixelRect(0, 0, 490, 900));

        Assert.Equal(565, t.ItemPower);
        Assert.Equal(231, t.DamagePerSecond);
        Assert.Equal((168, 252), t.DamagePerHit);
        Assert.Equal(1.10, t.AttacksPerSecond);
        Assert.Equal(1, t.EmptySockets);
        Assert.Equal(2, t.Lines.Count); // life and attack speed; delta pieces, range fragment, lost properties and hints dropped
        Assert.All(t.Lines, l => Assert.True(l.IsMatched, l.Text));
    }

    internal static ItemTooltip ParseRing()
    {
        var catalog = LoadCatalog();
        var lines = RingOcr.Select((text, i) => new TextLine(text, new PixelRect(10, 20 + i * 30, 400, 24))).ToList();
        return new TooltipParser(catalog).Parse(lines, new PixelRect(0, 0, 490, 900));
    }

    [Fact]
    public void ReadsHeader()
    {
        var tooltip = ParseRing();

        Assert.Equal("Virtuous Axolotl's Smile", tooltip.Name);
        Assert.Equal("Legendary", tooltip.Rarity);
        Assert.Equal("Ring", tooltip.ItemType);
        Assert.True(tooltip.Ancestral);
        Assert.Equal(800, tooltip.ItemPower);
        Assert.Equal(60, tooltip.RequiredLevel);
        Assert.True(tooltip.IsEquipped); // the OCR starts with the "EQUIPPED" banner
        Assert.Equal(15, tooltip.Season);
    }

    [Fact]
    public void MatchesAffixLinesWithValues()
    {
        var lines = ParseRing().Lines;
        double[] Value(string key) => [.. lines.Single(l => l.Matches.Any(m => m.Entry.Keys[0] == key)).Matches[0].Values];

        Assert.Equal([139.0], Value("Resistance_Jewelry_All"));
        Assert.Equal([895.0], Value("S04_Life"));
        Assert.Equal([9.8], Value("S04_AttackSpeed"));         // icon glyph "o" stripped, bracket misread as 1
        Assert.Equal([3.7], Value("S04_CritChance"));
        Assert.Equal([2698.0], Value("S04_Resistance_Single_Poison")); // value and text on separate OCR lines
    }

    [Fact]
    public void JoinsWrappedAspectParagraph()
    {
        var aspect = Assert.Single(ParseRing().Lines, l => l.Matches.Any(m => m.Entry.Kind == AffixKind.Aspect));

        Assert.Equal([55.0], aspect.Matches[0].Values);
        Assert.StartsWith("Imprinted:", aspect.Text);
        Assert.EndsWith("for 7 seconds.", aspect.Text);
    }

    [Fact]
    public void KeepsUnknownLinesAndStopsAtFooter()
    {
        var ring = ParseRing();

        // Its socketed runeword is no affix: it's what's socketed.
        Assert.DoesNotContain(ring.Lines, l => !l.IsMatched);
        Assert.Contains("Selachian Fortress", Assert.Single(ring.Socketed));
        Assert.DoesNotContain(ring.Lines, l => l.Text.Contains("Sell Value"));
    }
}
