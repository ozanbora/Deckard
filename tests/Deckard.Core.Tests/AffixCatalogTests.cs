using System.Text;
using Deckard.Core.Items;

namespace Deckard.Core.Tests;

public class AffixTemplateTests
{
    [Theory]
    [InlineData("+#% Critical Strike Chance", "+12.5% Critical Strike Chance", new[] { 12.5 })]
    [InlineData("+#% Critical Strike Chance", "12.5 % critical strike chance", new[] { 12.5 })] // OCR lost the sign, odd spacing
    [InlineData("+# Maximum Life", "+1,284 Maximum Life", new[] { 1284.0 })]
    [InlineData("+# Maximum Life", "+1,284 Maximum Life [1,100 - 1,400]", new[] { 1284.0 })] // advanced tooltip range
    // Lines below are from a real Season 15 tooltip capture.
    [InlineData("+#% Healing Received", "+11.5% Healing Received [7.0 - 11.0]%", new[] { 11.5 })]
    [InlineData("+# All Stats", "+100 All Stats +[100]", new[] { 100.0 })]
    [InlineData("+# Cold Resistance", "+2,045 Cold Resistance [1,600 - 1,799]", new[] { 2045.0 })]
    // Windows OCR output for the same tooltips: the closing bracket is read as 1, or the range is cut off.
    [InlineData("+#% Healing Received", "+11.5% Healing Received [7.0 - 11.01%", new[] { 11.5 })]
    [InlineData("+#% Attack Speed", "+9.8% Attack speed [5.0 - 8.01%", new[] { 9.8 })]
    [InlineData("+#% Damage", "+9% Damage [6 -", new[] { 9.0 })]
    [InlineData("+# Maximum Life", "+1, 110 Maximum Life", new[] { 1110.0 })]
    // Comparison view (inventory/stash against the equipped item).
    [InlineData("+#% Vulnerable Damage", "+8% Vulnerable Damage [8 - 8]% (+8%)", new[] { 8.0 })]
    [InlineData("+#% Basic Damage", "+24.0% Basic Damage [13.5 - 24.0]% (", new[] { 24.0 })]
    [InlineData("+#% Basic Damage", "+24.0% Basic Damage [13.5 - 24.0]% ( +24.0%)", new[] { 24.0 })]
    [InlineData("# All Resist", "74 All Resist (-21.4% Toughness)", new[] { 74.0 })]
    [InlineData("+#% Damage while Berserking", "+8.5% Damage while Berserking [5.0 - 8.5]% (@ Only)", new[] { 8.5 })]
    [InlineData("x#% Critical Strike Damage Multiplier", "x22% Critical Strike Damage Multiplier", new[] { 22.0 })]
    [InlineData("x#% Critical Strike Damage Multiplier", "22%[x] Critical Strike Damage Multiplier", new[] { 22.0 })]
    [InlineData("Lucky Hit: Up to a #% Chance to Weaken Enemies for # Seconds", "Lucky Hit: Up to a 15% Chance to Weaken Enemies for 2 Seconds", new[] { 15.0, 2.0 })]
    [InlineData("+# to Core Skills", "• +2 to Core Skills", new[] { 2.0 })]
    public void Matches_TooltipLines(string template, string line, double[] expected)
    {
        Assert.True(new AffixTemplate(template).TryMatch(line, out var values));
        Assert.Equal(expected, values);
    }

    [Theory]
    [InlineData("+# Fire Resistance", "+120 Cold Resistance")]
    [InlineData("+# Maximum Life", "+120 Maximum Life per Second")]
    [InlineData("+#% Attack Speed", "Attack Speed")]
    public void Rejects_OtherLines(string template, string line)
    {
        Assert.False(new AffixTemplate(template).TryMatch(line, out _));
    }

    [Theory]
    [InlineData("x#% Vulnerable Damage", "Vulnerable Damage 22%")]
    [InlineData("x#% Vulnerable Damage", "x22%[x] Vulnerable Damage")]
    public void Normalize_GivesTemplatesAndLinesTheSameShape(string template, string line)
    {
        Assert.Equal("#% Vulnerable Damage", AffixTemplate.Normalize(template));
        Assert.DoesNotContain("x", AffixTemplate.Normalize(line).Replace("Vulnerable", ""));
    }

    [Fact]
    public void Counts_RolledValues()
    {
        Assert.Equal(2, new AffixTemplate("+#% Movement Speed for # Seconds After Evading").ValueCount);
        Assert.Equal(0, new AffixTemplate("Indestructible").ValueCount);
    }
}

public class AffixCatalogTests
{
    [Fact]
    public void NamesSeasonalSocketables()
    {
        var catalog = Catalog();

        Assert.Equal("Abyssal Splinter of Anguish", catalog.Socketables["S15_SoulSplinter_Andariel_04"].Name);
        Assert.Empty(AffixCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(Json.Replace("\"socketables\"", "\"unused\"")))).Socketables);
    }

    [Fact]
    public void NamesMercenariesAndTheirTreeSkills()
    {
        var varyana = Catalog().Mercenaries["MercenaryClass_BerserkerCrone"];

        Assert.Equal("Varyana", varyana.Name);
        Assert.Equal("Cleave", varyana.Skills[varyana.Nodes[562]].Name);
        Assert.Equal(["cluster", "skill"], varyana.Tree!.Nodes.Select(n => n.Kind)); // drawn like a class's tree
        Assert.Empty(AffixCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(Json.Replace("\"mercenaries\"", "\"unused\"")))).Mercenaries); // older files
    }

    private const string Json = """
        {
          "schemaVersion": 8, "game": "d4", "season": 15, "gameBuild": "3.2.1.73552", "locale": "enus",
          "classes": ["Sorcerer", "Druid"],
          "socketables": { "S15_SoulSplinter_Andariel_04": { "name": "Abyssal Splinter of Anguish", "icon": 2271106774 } },
          "mercenaries": { "MercenaryClass_BerserkerCrone": { "name": "Varyana", "nodes": { "562": "Mercenary_BerserkerCrone_Cleave" },
            "skills": { "Mercenary_BerserkerCrone_Cleave": { "name": "Cleave", "desc": "Varyana cleaves." } },
            "tree": { "nodes": [ { "node": 565, "kind": "cluster", "skill": "", "name": "", "maxRanks": 0, "requiredLevel": 2, "icon": 0, "x": 0, "y": -1264 },
              { "node": 562, "kind": "skill", "skill": "Mercenary_BerserkerCrone_Cleave", "name": "Cleave", "maxRanks": 1, "requiredLevel": 2, "icon": 7, "x": -1250, "y": -670 } ],
              "links": [ { "a": 565, "b": 562, "corners": [] } ] } } },
          "entries": [
            { "kind": "affix", "keys": ["Resistance_Fire"], "ids": [1], "lines": ["+# Fire Resistance"],
              "prefix": "Dampening", "suffix": "of Dampening", "itemTypes": ["Helm"], "classes": ["Sorcerer", "Druid"] },
            { "kind": "implicit", "keys": ["INHERENT_Resistance_Ring_Fire"], "ids": [2], "lines": ["+# Fire Resistance"], "itemTypes": ["Ring"] },
            { "kind": "affix", "keys": ["Resistance_Mixed"], "ids": [3], "lines": ["+# Resistance to All Elements", "+# Fire Resistance"] },
            { "kind": "aspect", "keys": ["legendary_generic_043"], "ids": [4], "name": "Rapid Aspect",
              "lines": ["Basic Skills gain +#% Attack Speed."], "classes": ["Sorcerer"] }
          ],
          "paragon": {
            "boards": { "Paragon_Paladin_00": { "name": "Start", "width": 2, "nodes": ["Generic_Normal_Str", null, "Generic_Gate", "Generic_Normal_Str"] } },
            "nodes": {
              "Generic_Normal_Str": { "name": "Strength", "kind": "normal", "lines": ["+5 Strength"], "icon": 635104014, "iconIsMask": true },
              "Generic_Gate": { "name": "Board Attachment Gate", "kind": "gate", "lines": ["+5 Strength"], "icon": 3778111510, "iconIsMask": true }
            },
            "glyphs": { "Rare_107_Strength_Main": { "name": "Resplendence", "lines": ["Increase damage by x#%."] } }
          },
          "affixText": {
            "10": { "lines": ["+#% Attack Speed"], "slots": [[0, 100, 1]], "magicType": 0, "ranges": [[[0, 0.05, 0.08]]] },
            "11": { "lines": ["+# Maximum Life"], "slots": [[0, 1, -1]], "magicType": 0, "ranges": [[[0, 100, 200], [800, 1000, 1500]]] },
            "12": { "lines": ["Deal x#% damage for # seconds"], "slots": [[1, 1, -1], null], "magicType": 1, "ranges": [null, [[0, 10, 15]]] },
            "13": { "lines": ["Gain x#% damage"], "slots": [[0, 1, -1]], "magicType": 2, "ranges": [[[0, 15, 35]]] }
          },
          "unresolvedKeys": []
        }
        """;

    private static AffixCatalog Catalog() => AffixCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(Json)));

    [Fact]
    public void Load_ReadsSeasonInfo()
    {
        var catalog = Catalog();
        Assert.Equal(15, catalog.Season);
        Assert.Equal("3.2.1.73552", catalog.GameBuild);
        Assert.Equal(4, catalog.Entries.Count);
        Assert.Equal(AffixKind.Aspect, catalog.Entries[3].Kind);
        Assert.Equal("Rapid Aspect", catalog.Entries[3].Name);
    }

    [Fact]
    public void SkillNodeDescribesEachRank()
    {
        var node = new SkillNode(1, "skill", "Warlock_Summon_Lunatic", "Lunatic", 5, 0, 0, 0, 0, "deals #% damage",
            ["deals 80% damage", "deals 88% damage"]);

        Assert.Equal("deals 80% damage", node.DescAt(0));  // unused: rank 1
        Assert.Equal("deals 88% damage", node.DescAt(2));
        Assert.Equal("deals 88% damage", node.DescAt(15)); // past the listed ranks: the last one
        Assert.Equal("plain", (node with { Desc = "plain", DescByRank = null }).DescAt(3));
    }

    [Fact]
    public void AffixText_FillsRolledValuesAsTheGameShowsThem()
    {
        var text = Catalog().AffixText;

        Assert.Equal(["+8.0% Attack Speed"], text[10].Fill([0.08]));      // fraction scaled to percent, one decimal
        Assert.Equal(["+1,500 Maximum Life"], text[11].Fill([1500]));
        Assert.Equal(["Deal x12.5% damage for # seconds"], text[12].Fill([3, 12.5])); // second value; no slot keeps #
        Assert.Equal(["+# Maximum Life"], text[11].Fill([]));                // value missing
        Assert.Equal(1, text[12].MagicType);
    }

    [Fact]
    public void AffixText_ShowsRollRangesWhereTheGameDoes()
    {
        var text = Catalog().AffixText;

        Assert.Equal(["+8.0% Attack Speed [5.0 - 8.0]%"], text[10].Fill([0.08], rangesAt: 800));          // affix: at the end
        Assert.Equal(["Deal x12.5% [10 - 15]% damage for # seconds"], text[12].Fill([3, 12.5], rangesAt: 800)); // aspect: after the number
        Assert.Equal(["+1,500 Maximum Life [2,000 - 3,000]"], text[11].Fill([1500], rangesAt: 900, rangeScale: 2)); // tier by power, two-handed
        Assert.Equal(["+150 Maximum Life [100 - 200]"], text[11].Fill([150], rangesAt: 500));
        Assert.Equal(["+1,500 Maximum Life"], text[11].Fill([1500], rangesAt: 0));      // power unknown and the range depends on it
        Assert.Equal(["+8.0% Attack Speed [5.0 - 8.0]%"], text[10].Fill([0.08], rangesAt: 0)); // the same at every power
        Assert.Equal(["Gain x50% damage"], text[13].Fill([50], rangesAt: 800));          // unique powers don't follow the formulas
        Assert.Equal(["+8.0% Attack Speed"], text[10].Fill([0.08]));
    }

    [Fact]
    public void Load_ReadsParagonBoards()
    {
        var paragon = Catalog().Paragon;

        var board = paragon.Boards["Paragon_Paladin_00"];
        Assert.Equal(2, board.Width);
        Assert.Null(board.Nodes[1]);
        Assert.Equal("gate", paragon.Nodes[board.Nodes[2]!].Kind);
        Assert.Equal(["+5 Strength"], paragon.Nodes["Generic_Normal_Str"].Lines);
        Assert.Equal("Resplendence", paragon.Glyphs["Rare_107_Strength_Main"].Name);
    }

    [Fact]
    public void Match_ReturnsEveryCandidateWithValues()
    {
        var matches = Catalog().Match("+95 Fire Resistance");

        Assert.Equal(3, matches.Count);
        Assert.All(matches, m => Assert.Equal([95.0], m.Values));
        Assert.Contains(matches, m => m.Entry.Kind == AffixKind.Implicit);
        Assert.Contains(matches, m => m.Entry.Keys[0] == "Resistance_Mixed" && m.LineIndex == 1);
    }

    [Fact]
    public void Match_FindsAspectText()
    {
        var match = Assert.Single(Catalog().Match("Basic Skills gain +18% Attack Speed."));
        Assert.Equal("Rapid Aspect", match.Entry.Name);
        Assert.Equal([18.0], match.Values);
    }

    [Fact]
    public void Match_UnknownLineReturnsNothing()
    {
        Assert.Empty(Catalog().Match("Requires Level 60"));
    }

    [Fact]
    public void Load_RejectsOtherSchemaVersions()
    {
        var json = Json.Replace("\"schemaVersion\": 8", "\"schemaVersion\": 7");
        var error = Assert.Throws<InvalidDataException>(() => AffixCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json))));
        Assert.Contains("schema 7", error.Message);
    }

    [Fact]
    public void DataRevision_TellsOlderExtractions()
    {
        Assert.True(Catalog().IsOutdated); // no revision: written before revisions
        var current = Json.Replace("\"schemaVersion\": 8,", $"\"schemaVersion\": 8, \"dataRevision\": {AffixCatalog.ExpectedDataRevision},");
        Assert.False(AffixCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(current))).IsOutdated);
    }

    [Fact]
    public void LoadSeason_PicksNewestSeason()
    {
        var root = Directory.CreateTempSubdirectory("deckard-affixes-").FullName;
        try
        {
            foreach (var season in new[] { 14, 15 })
            {
                Directory.CreateDirectory(Path.Combine(root, $"season-{season}"));
                File.WriteAllText(Path.Combine(root, $"season-{season}", AffixCatalog.FileName),
                    Json.Replace("\"season\": 15", $"\"season\": {season}"));
            }

            Assert.Equal([14, 15], AffixCatalog.AvailableSeasons(root));
            Assert.Equal(15, AffixCatalog.LoadSeason(root).Season);
            Assert.Equal(14, AffixCatalog.LoadSeason(root, 14).Season);
            Assert.Throws<FileNotFoundException>(() => AffixCatalog.LoadSeason(root, 16));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
