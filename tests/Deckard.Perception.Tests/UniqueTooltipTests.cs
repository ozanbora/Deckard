using System.Globalization;
using System.Text;
using Deckard.Core.Capture;
using Deckard.Core.Items;
using Deckard.Perception.Ocr;
using Deckard.Perception.Tooltips;

namespace Deckard.Perception.Tests;

/// <summary>
/// 1080p equipped items as OCR read them: a unique whose power OCR mangled (merged words, a dropped "10%[+]") followed
/// by its flavor quote, and legendaries with a socketed runeword, which the season data doesn't describe.
/// </summary>
public class UniqueTooltipTests
{
    private const string Catalog = """
        {
          "schemaVersion": 8, "game": "d4", "season": 15, "gameBuild": "3.2.1.73552", "locale": "enus", "classes": [],
          "entries": [
            { "kind": "affix", "keys": ["Armor"], "ids": [1], "lines": ["+# Armor"] },
            { "kind": "affix", "keys": ["Strength"], "ids": [2], "lines": ["+# Strength"] },
            { "kind": "affix", "keys": ["Life"], "ids": [3], "lines": ["+# Maximum Life"] },
            { "kind": "affix", "keys": ["Willpower"], "ids": [6], "lines": ["+# Willpower"] },
            { "kind": "affix", "keys": ["X2_CoreStats_All"], "ids": [4], "lines": ["# All Stats"], "fixed": true },
            { "kind": "unique", "keys": ["Gloves_Unique_Paladin_003"], "ids": [5], "name": "Dawnfire", "itemType": "Gloves",
              "lines": ["Holy Light's passive now deals # Fire Damage every second in a small area around you. Each time an enemy dies within this aura, it increases its damage by 15%[x] and burn area by 10%[+] for 20 seconds, up to 10 times. Activating Holy Light Aura refreshes this duration."] }
          ],
          "goods": [
            { "key": "Gem_Sapphire_03", "id": 7, "name": "Chipped Sapphire", "kind": "Gem", "lines": ["Weapon: x16% Critical Strike Damage", "Armor: +20 Willpower", "Jewelry: +250 Cold Resistance"] }
          ]
        }
        """;

    private static ItemTooltip Parse(params (string Text, int X)[] rows)
    {
        var catalog = AffixCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(Catalog)));
        // Pieces with the same row index share a row, as OCR splits a line where its color changes.
        var y = 0;
        var lines = new List<TextLine>();
        foreach (var (text, x) in rows)
        {
            if (x == 10)
                y += 30;
            lines.Add(new TextLine(text, new PixelRect(x, y, 8 * text.Length, 24)));
        }
        return new TooltipParser(catalog).Parse(lines, new PixelRect(0, 0, 371, 861));
    }

    [Fact]
    public void ReadsAMangledUniquePowerAndSkipsItsQuote()
    {
        var t = Parse(
            ("EQUIPPED", 10), ("DAWNFIRE", 10), ("Unique Gloves", 10), ("750 Item Power", 10), ("25 (A +25) Quality", 10),
            ("702 Armor", 10), ("+91 Strength +173 - 831", 10), ("+480 Maximum Life [351 - 540]", 10), ("+100A11Stats+[1001", 10),
            ("+ HolyLight's passive now deals 4,152", 10), ("[3,598 - 4,1521 Fire Damage every", 10),
            ("second in a small area around you.", 10), ("Each time an enemy dies within this", 10),
            ("aura, it increases its damage by 15%", 10), ("and burn area by for 20", 10), ("seconds, up to 10 times.", 10),
            ("Activating Holy Light Aura refreshes", 10), ("this duration.", 10),
            ("\"Kindle your inner flame. With it, we are the", 10), ("dawn that bleaks the night.\" - Paladin Justelle", 10),
            ("Requires Level 60", 10), ("Account Bound", 10));

        Assert.Equal("Dawnfire", t.Name);
        Assert.All(t.Lines, l => Assert.True(l.IsMatched, $"unmatched: {l.Text}"));
        // "702 Armor" is the gloves' base armor, not an affix.
        Assert.Equal(["Strength", "Life", "X2_CoreStats_All", "Gloves_Unique_Paladin_003"], t.Lines.Select(l => l.Matches[0].Entry.Keys[0]));
        Assert.Equal([91.0], t.Lines[0].Matches[0].Values); // "+173 - 831" is the roll range "[73 - 83]"
        Assert.Equal([4152.0], t.Lines[3].Matches[0].Values);
        Assert.True(t.NameMatchesPower);
        Assert.Equal(60, t.RequiredLevel);
    }

    [Fact]
    public void ReadsSocketedGemsAsSocketsNotAffixes()
    {
        // A helm with its own +32 Willpower and two Chipped Sapphires, each giving +20 Willpower in armor.
        var t = Parse(
            ("PYROCLASTIC WARLORD", 10), ("GREATHELM", 10), ("Legendary Helm", 10), ("527 Item Power", 10), ("790 Armor", 10),
            ("+32 Willpower [31 - 37]", 10), ("+16 Maximum Life [11 - 18]", 10),
            ("20 Willpower", 10), ("20 Willpower", 10), ("Requires Level 48", 10));

        Assert.Equal(["Willpower", "Life"], t.Lines.Select(l => l.Matches[0].Entry.Keys[0]));
        Assert.Equal(["20 Willpower", "20 Willpower"], t.Socketed);
    }

    [Fact]
    public void ReadsARunewordAsOneLine()
    {
        var t = Parse(
            ("EQUIPPED", 10), ("ARCHON HELrn OF", 10), ("ANGELIC", 10), ("mASTERWORK", 10), ("Ancestral Legendary Helm", 10),
            ("800 Item Power", 10), ("1 ,500 Armor", 10), ("0 +863 Armor [689-7791", 10),
            ("@BacQue", 10), ("(50/300) -", 90), ("Selachian Fortress", 180), ("Travel 30 meters.", 10),
            ("(Overflow: Increased Duration)", 10),
            ("@NeoCeh Graceful Alpha", 10), ("Canid", 10), ("Avoid health damage while fighting", 10),
            ("for 2 seconds. (1 time)", 10), ("(Overflow: Summon Multiple Wolves)", 10),
            ("ZE Scroll Down lires Level 60", 10), ("Account Bound", 10));

        // "1 ,500 Armor" is the helm's base armor, not an affix; the runewords are what's socketed, not affixes.
        Assert.Equal(["863"], t.Lines.Select(l => l.IsMatched ? l.Matches[0].Values[0].ToString(CultureInfo.InvariantCulture) : l.Text));
        Assert.Equal(
            ["BacQue (50/300) - Selachian Fortress Travel 30 meters. (Overflow: Increased Duration)",
             "NeoCeh Graceful Alpha Canid Avoid health damage while fighting for 2 seconds. (1 time) (Overflow: Summon Multiple Wolves)"],
            t.Socketed);
        Assert.True(t.Truncated);
    }
}
