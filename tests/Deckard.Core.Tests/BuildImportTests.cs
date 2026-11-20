using System.Text;
using System.Text.Json.Nodes;
using Deckard.Core.Builds;
using Deckard.Core.Items;

namespace Deckard.Core.Tests;

public class BuildImportTests
{
    // Just enough of a season's data for the names the two sites write.
    private const string CatalogJson = """
        {
          "schemaVersion": 8, "game": "d4", "season": 15, "gameBuild": "3.2.1.73552", "locale": "enus",
          "classes": ["Sorcerer", "Warlock"],
          "items": {
            "Gloves_Normal_Generic_001": { "name": "Gloves", "type": "Gloves" },
            "Pants_Unique_Generic_100": { "name": "Temerity", "type": "Pants", "unique": true },
            "Talisman_Charm_Set_Warlock_02_04": { "name": "Linta of Abaddon's Flesh", "type": "Charm" }
          },
          "goods": [{ "key": "Gem_Ruby_04", "id": 1, "name": "Ruby", "kind": "Gem" }],
          "skills": { "Warlock_BurningSkull": "Blazing Scream", "Sorcerer_Hydra": "Hydra" },
          "skillTrees": {
            "Warlock": { "nodes": [
              { "node": 607, "kind": "modifier", "skill": "Warlock_SigilOfShadows", "name": "Sigil of Subversion: Slow", "maxRanks": 5, "requiredLevel": 2, "x": 0, "y": 0, "icon": 0 },
              { "node": 807, "kind": "skill", "skill": "Warlock_BurningSkull", "name": "Blazing Scream", "maxRanks": 15, "requiredLevel": 1, "x": 0, "y": 0, "icon": 0 }
            ], "links": [] }
          },
          "entries": [
            { "kind": "affix", "keys": ["S04_Damage_to_Vulnerable", "X2_Damage_to_Vulnerable_Greater"], "ids": [1834112, 2602170],
              "lines": ["x#% Vulnerable Damage Multiplier"], "itemTypes": ["Amulet", "Gloves", "Ring"] },
            { "kind": "affix", "keys": ["X2_SkillRankBonus_Warlock_Category_Hellfire"], "ids": [2534852], "lines": ["+# to Hellfire Skills"], "itemTypes": ["Boots"] },
            { "kind": "tempered", "keys": ["Tempered_Life"], "ids": [1862327], "lines": ["+# Maximum Life"] },
            { "kind": "aspect", "keys": ["legendary_warlock_014_x2"], "ids": [2547932], "name": "Aspect of Ignition", "lines": ["Skills empowered by Volatility deal more damage."] },
            { "kind": "unique", "keys": ["unique_temerity"], "ids": [1900001], "name": "Temerity", "lines": ["Effects that heal you beyond full Life grant a Barrier."] }
          ],
          "paragon": {
            "boards": {
              "Paragon_Warlock_00": { "name": "Start", "width": 2, "nodes": ["Generic_Normal_Int", "Generic_Gate", "StartNode", null] },
              "Paragon_Warlock_02": { "name": "Demonic Spicules", "width": 2, "nodes": ["Generic_Gate", null, null, "Generic_Normal_Int"] },
              "Paragon_Sorc_02": { "name": "Demonic Spicules", "width": 2, "nodes": [null, null, null, null] }
            },
            "nodes": {
              "Generic_Normal_Int": { "name": "Intelligence", "kind": "normal", "lines": ["+5 Intelligence"], "icon": 1, "iconIsMask": true },
              "Generic_Gate": { "name": "Board Attachment Gate", "kind": "gate", "lines": [], "icon": 2, "iconIsMask": true },
              "StartNode": { "name": "Start", "kind": "start", "lines": [], "icon": 3, "iconIsMask": true }
            },
            "glyphs": { "Rare_101_Intelligence_Main": { "name": "Hellforge", "lines": ["Hellfire damage."] } }
          },
          "unresolvedKeys": []
        }
        """;

    private static AffixCatalog Catalog() => AffixCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(CatalogJson)));

    [Theory]
    [InlineData("https://d4builds.gg/builds/blazing-scream-warlock-endgame/", "/builds/blazing-scream-warlock-endgame/", null)]
    [InlineData("https://d4builds.gg/builds/blazing-scream-warlock-leveling/?var=2", "/builds/blazing-scream-warlock-leveling/", 2)]
    public void ParsesD4BuildsLinks(string link, string path, int? variant) =>
        Assert.Equal((path, variant), D4BuildsImport.ParseLink(link));

    [Fact]
    public void ParsesIcyVeinsPlannerLinks()
    {
        Assert.Equal("3d2ff557d411", IcyVeinsImport.ParseLink("https://www.icy-veins.com/d4/planner/?uid=3d2ff557d411"));
        Assert.Null(IcyVeinsImport.ParseLink("https://www.icy-veins.com/d4/guides/hellfire-blazing-scream-warlock-build/"));
    }

    [Fact]
    public async Task TurnsDownIcyVeinsGuidePagesWithoutFetching()
    {
        using var http = new HttpClient(new NoNetwork());
        var error = await Assert.ThrowsAsync<FormatException>(() =>
            BuildPlanLoader.LoadAsync("https://www.icy-veins.com/d4/guides/hellfire-blazing-scream-warlock-build/", http, Catalog()));
        Assert.Contains("planner", error.Message);
    }

    [Fact]
    public void ParsesD4BuildsDocuments()
    {
        var variant = new JsonObject
        {
            ["class"] = "Warlock",
            ["variantName"] = "Starter",
            ["gear"] = new JsonObject { ["Gloves"] = "Aspect of Ignition", ["Pants"] = "Temerity" },
            ["newStats"] = new JsonObject { ["Gloves"] = new JsonArray(null, "Vulnerable Damage Multiplier") },
            ["greaterAffixes"] = new JsonObject { ["Gloves"] = new JsonArray(null, 1) },
            ["temperingStats"] = new JsonObject { ["Gloves"] = new JsonArray("Maximum Life (Worldly Endurance - Defensive)") },
            ["newGems"] = new JsonObject { ["Gloves"] = new JsonArray("Ruby", null) },
            ["charms"] = new JsonArray(new JsonObject { ["name"] = "Linta of Abaddon's Flesh" }),
            ["charmStats"] = new JsonObject { ["0"] = new JsonArray("Hellfire Skills", null) },
            ["skills"] = new JsonArray("Blazing Scream", "Hydra-v2"),
            ["notes"] = "Leveling #[https://d4builds.gg/builds/x/][Build]#",
            ["paragon"] = new JsonObject
            {
                ["boards"] = new JsonArray(
                    new JsonObject { ["name"] = "Starting Board", ["x"] = 0, ["y"] = 0, ["rotation"] = 0, ["glyph"] = "Hellforge", ["glyphLevel"] = 100 },
                    new JsonObject { ["name"] = "Demonic Spicules", ["x"] = 0, ["y"] = 1, ["rotation"] = 540 }),
                ["selectedTiles"] = new JsonArray("startingboard_r1c1", "demonicspicules_r2c2"),
            },
        };
        var doc = (JsonObject)variant.DeepClone();
        doc["name"] = "Blazing Scream Warlock (S15) ";
        doc["season"] = 15;
        doc["variants"] = new JsonArray(new JsonObject { ["index"] = 1, ["class"] = "Warlock", ["variantName"] = "Endgame" });

        var plan = D4BuildsImport.Parse(Firestore(doc), Catalog(), variant: 1, id: "9d65");

        Assert.Equal("Blazing Scream Warlock (S15)", plan.Name);
        Assert.Equal(["Starter", "Endgame"], plan.Variants.Select(v => v.Name));
        Assert.Equal(1, plan.DefaultVariant);
        var starter = plan.Variants[0];
        Assert.Equal(1, starter.ClassIndex);
        Assert.Equal(["Warlock_BurningSkull", "Sorcerer_Hydra"], starter.SkillBar); // "-v2" dropped; another class's skill is still the only match
        Assert.Equal("Leveling Build", starter.SkillNotes);

        var gloves = starter.Items[13];
        Assert.Equal("Gloves_Normal_Generic_001", gloves.ItemKey); // a stand-in base: the build names only the aspect
        Assert.Equal([2547932L], gloves.Aspects.Select(a => a.Id));
        Assert.Equal([new PlannedAffix(1834112, true)], gloves.Affixes.Select(a => a with { Values = null }));
        Assert.Equal([1862327L], gloves.Tempered.Select(a => a.Id));
        Assert.Equal(["Gem_Ruby_04"], gloves.Sockets!);
        Assert.Equal("Pants_Unique_Generic_100", starter.Items[14].ItemKey);
        Assert.Equal([1900001L], starter.Items[14].Aspects.Select(a => a.Id));
        Assert.Equal("Talisman_Charm_Set_Warlock_02_04", starter.Items[21].ItemKey);
        Assert.Equal([2534852L], starter.Items[21].Affixes.Select(a => a.Id));

        var boards = starter.ParagonSteps.Single().Boards;
        Assert.Equal(("Paragon_Warlock_00", 0, 0, 0), (boards[0].Id, boards[0].Rotation, boards[0].X, boards[0].Y));
        Assert.Equal(("Rare_101_Intelligence_Main", 100), (boards[0].Glyph, boards[0].GlyphLevel));
        Assert.Equal([0], boards[0].Nodes);
        Assert.Equal(("Paragon_Warlock_02", 2, 0, -1), (boards[1].Id, boards[1].Rotation, boards[1].X, boards[1].Y)); // y up there, down here
        Assert.Equal([3], boards[1].Nodes);
    }

    [Fact]
    public void ParsesIcyVeinsBuilds()
    {
        var json = new JsonObject
        {
            ["build"] = new JsonObject
            {
                ["uid"] = "3d2ff557d411",
                ["name"] = "Hellfire Blazing Scream Warlock",
                ["gear_data"] = Sets(new JsonObject
                {
                    ["id"] = "a", ["name"] = "Starter", ["class"] = "warlock",
                    ["itemIds"] = new JsonObject { ["gloves"] = "Gloves_Normal_Generic_001_warlock", ["pants"] = "Pants_Unique_Generic_100" },
                    ["equippedGear"] = new JsonObject { ["gloves"] = "Gloves", ["pants"] = "Temerity" },
                    ["itemAspects"] = new JsonObject { ["gloves"] = "Aspect of Ignition" },
                    ["itemPowers"] = new JsonObject { ["gloves"] = 850 },
                    ["itemUserAffixes"] = new JsonObject { ["gloves"] = new JsonArray(new JsonObject { ["value"] = 28, ["affixId"] = "2602170" }, new JsonObject { ["affixId"] = "Unknown_Key" }) },
                    ["itemTemperingAffix"] = new JsonObject { ["gloves"] = new JsonObject { ["value"] = 40, ["affixId"] = "1862327" } },
                    ["itemSockets"] = new JsonObject { ["gloves"] = new JsonArray("Ruby") },
                }),
                ["skills_data"] = Sets(new JsonObject
                {
                    ["id"] = "a",
                    ["equippedSkills"] = new JsonObject { ["skill2"] = "Warlock_SigilOfShadows", ["skill1"] = "Warlock_BurningSkull" },
                    ["skillPoints"] = new JsonObject { ["skillPointDistribution"] = new JsonObject { ["1"] = new JsonObject { ["spentPoints"] = 5 }, ["9"] = new JsonObject { ["spentPoints"] = 1 } } },
                }),
                ["paragon_data"] = Sets(new JsonObject
                {
                    ["id"] = "a",
                    ["paragonBoard"] = new JsonObject
                    {
                        ["connectedBoards"] = new JsonObject
                        {
                            ["demonic-spicules"] = new JsonObject { ["position"] = new JsonObject { ["col"] = 1, ["row"] = -1 }, ["rotation"] = 270 },
                        },
                        ["nodeStates"] = new JsonObject
                        {
                            ["1-1"] = new JsonObject { ["state"] = "active" },
                            ["1-2"] = new JsonObject { ["state"] = "available" },
                            ["demonic-spicules-2-2"] = new JsonObject { ["state"] = "active" },
                        },
                        ["equippedGlyphs"] = new JsonObject { ["1-1"] = new JsonObject { ["id"] = "hellforge", ["level"] = 25 } },
                    },
                }),
            },
        }.ToJsonString();

        var plan = IcyVeinsImport.Parse(json, Catalog());

        Assert.Equal(("3d2ff557d411", "Hellfire Blazing Scream Warlock", 15), (plan.Id, plan.Name, plan.Season));
        var v = plan.Variants.Single();
        Assert.Equal(("Starter", 1), (v.Name, v.ClassIndex));
        Assert.Equal(["Warlock_BurningSkull", "Warlock_SigilOfShadows"], v.SkillBar);
        Assert.Equal(new Dictionary<int, int> { [807] = 5 }, v.SkillSteps.Single().RanksByNode); // index 9 is past the tree

        var gloves = v.Items[13];
        Assert.Equal(("Gloves_Normal_Generic_001", 850), (gloves.ItemKey, gloves.Power));
        Assert.Equal([new PlannedAffix(2602170, true)], gloves.Affixes.Select(a => a with { Values = null }));
        Assert.Equal([1862327L], gloves.Tempered.Select(a => a.Id));
        Assert.Equal([2547932L], gloves.Aspects.Select(a => a.Id));
        Assert.Equal(["Gem_Ruby_04"], gloves.Sockets!);
        Assert.Equal([1900001L], v.Items[14].Aspects.Select(a => a.Id)); // the unique's power, from its name

        var boards = v.ParagonSteps.Single().Boards;
        Assert.Equal(("Paragon_Warlock_00", "Rare_101_Intelligence_Main", 25), (boards[0].Id, boards[0].Glyph, boards[0].GlyphLevel));
        Assert.Equal([0], boards[0].Nodes);
        Assert.Equal(("Paragon_Warlock_02", 3, 1, -1), (boards[1].Id, boards[1].Rotation, boards[1].X, boards[1].Y));
        Assert.Equal([3], boards[1].Nodes);
    }

    private static JsonObject Sets(JsonObject set) => new() { ["sets"] = new JsonArray(set) };

    /// <summary>A plain object as a Firestore document, in the REST API's typed values.</summary>
    private static string Firestore(JsonObject doc)
    {
        static JsonObject Typed(JsonNode? node) => node switch
        {
            null => new JsonObject { ["nullValue"] = null },
            JsonObject o => new JsonObject { ["mapValue"] = new JsonObject { ["fields"] = new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)Typed(p.Value)))) } },
            JsonArray a => new JsonObject { ["arrayValue"] = new JsonObject { ["values"] = new JsonArray([.. a.Select(x => (JsonNode)Typed(x))]) } },
            JsonValue v when v.TryGetValue<int>(out var n) => new JsonObject { ["integerValue"] = n.ToString() },
            _ => new JsonObject { ["stringValue"] = node.GetValue<string>() },
        };
        return new JsonObject { ["fields"] = Typed(doc)["mapValue"]!["fields"]!.DeepClone() }.ToJsonString();
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No requests expected.");
    }

    [Theory]
    // A guide page names its build's document; a player's own build shares one page and is the document its link names.
    [InlineData("https://d4builds.gg/builds/blazing-scream-warlock-endgame/", """{"result":{"pageContext":{"seoId":"guide-doc"}}}""", "guide-doc")]
    [InlineData("https://d4builds.gg/builds/75ab2cff-f838-42a1-a294-1543145ea43a/?var=0", """{"result":{"pageContext":{"gems":[]}}}""",
        "75ab2cff-f838-42a1-a294-1543145ea43a")]
    public async Task LoadsD4BuildsGuidesAndPlayersOwnBuilds(string link, string pageData, string document)
    {
        var doc = new JsonObject { ["name"] = "Build", ["class"] = "Warlock", ["variantName"] = "Endgame" };
        using var http = new HttpClient(new Pages(url =>
            url.Contains("/page-data/", StringComparison.Ordinal) ? pageData
            : url.EndsWith("/documents/builds/" + document, StringComparison.Ordinal) ? Firestore(doc)
            : throw new InvalidOperationException("Unexpected request: " + url)));

        var plan = await BuildPlanLoader.LoadAsync(link, http, Catalog());

        Assert.Equal(document, plan.Id);
        Assert.Equal(["Endgame"], plan.Variants.Select(v => v.Name));
    }

    [Fact]
    public async Task TurnsDownD4BuildsPagesThatNameNoBuild()
    {
        using var http = new HttpClient(new Pages(_ => """{"result":{"pageContext":{}}}"""));
        await Assert.ThrowsAsync<FormatException>(() => BuildPlanLoader.LoadAsync("https://d4builds.gg/builds/some-page/", http, Catalog()));
    }

    /// <summary>Answers each request with the text <paramref name="page"/> gives for its URL.</summary>
    private sealed class Pages(Func<string, string> page) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(page(request.RequestUri!.ToString())) });
    }
}
