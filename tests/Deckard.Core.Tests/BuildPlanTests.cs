using System.Text.Json;
using Deckard.Core.Builds;

namespace Deckard.Core.Tests;

public class BuildPlanTests
{
    // Shape of a planner profile response (planners.maxroll.gg/profiles/d4/<id>), trimmed to what Deckard reads.
    private static string Profile()
    {
        var data = new
        {
            season = 15,
            activeProfile = 1,
            items = new Dictionary<string, object>
            {
                ["3"] = new { id = "Helm_Unique_Generic_005", explicits = new[] { new { nid = 1829570, greater = true } } },
                ["17"] = new
                {
                    id = "1HShield_Legendary_Generic_001",
                    power = 800,
                    sockets = new[] { "Gem_Ruby_05" },
                    explicits = new object[] { new { nid = 1928803, greater = true }, new { nid = 1829560, values = new[] { 0.08 } } },
                    tempered = new[] { new { nid = 2611712 } },
                    aspects = new[] { new { nid = 2542658, values = new[] { 50.0 } } },
                },
            },
            profiles = new object[]
            {
                new { name = "Starter", @class = 6, level = 60, items = new Dictionary<string, int> { ["4"] = 3 }, skillBar = new[] { "Paladin_Purify" },
                    warlockShard = "Warlock_ClassMechanic_Legion", warlockFragment = "Warlock_ClassMechanic_Legion_B",
                    // As maxroll has it: a tree node at 0 is the pick it passed over; the trigger left unset is a placeholder key.
                    mercenary = new { id = "MercenaryClass_BerserkerCrone", tree = new Dictionary<string, int> { ["561"] = 1, ["564"] = 0 },
                        support = "MercenaryClass_ShieldBearer", supportSkills = new[] { "NPC_Mercenary_ShieldBearer_Bastion", "trigger_skill" } } },
                new
                {
                    name = "Endgame", @class = 6, level = 70,
                    items = new Dictionary<string, int> { ["4"] = 3, ["6"] = 17 },
                    skillBar = new[] { "Paladin_Fortress", "Paladin_HolyShock_Aura" },
                    skillTree = new { steps = new[] { new { name = "Endgame", data = new Dictionary<string, int> { ["1849"] = 15, ["1659"] = 1, ["1652"] = 0 } } } },
                    paragon = new
                    {
                        steps = new[]
                        {
                            new
                            {
                                name = "Level 150 Glyphs",
                                data = new object[]
                                {
                                    new { id = "Paragon_Paladin_00", nodes = new Dictionary<string, int> { ["10"] = 1, ["304"] = 1 }, rotation = 0, position = new { x = 0, y = 0 }, glyph = "Rare_107_Strength_Main", glyphLevel = 150 },
                                    new { id = "Paragon_Paladin_06", nodes = new Dictionary<string, int> { ["430"] = 1 }, rotation = 3, position = new { x = 0, y = -1 } },
                                },
                            },
                        },
                    },
                    widgetNotes = new
                    {
                        skills = new
                        {
                            root = new
                            {
                                children = new object[]
                                {
                                    new { type = "paragraph", children = new object[] { new { text = "Holy Light Aura" }, new { text = " is the main damage." } } },
                                    new { type = "paragraph", children = new object[] { new { text = "Use Fortress on bosses." } } },
                                },
                            },
                        },
                    },
                },
            },
        };
        return JsonSerializer.Serialize(new { id = "jj4c9y0q", name = "Auradin Paladin", data = JsonSerializer.Serialize(data) });
    }

    [Fact]
    public void Parses_VariantsItemsAndSkills()
    {
        var plan = BuildPlanLoader.Parse(Profile());

        Assert.Equal("jj4c9y0q", plan.Id);
        Assert.Equal(15, plan.Season);
        Assert.Equal(["Starter", "Endgame"], plan.Variants.Select(v => v.Name));
        Assert.Equal(1, plan.DefaultVariant); // the profile's active variant

        var endgame = plan.Variants[1];
        Assert.Equal(6, endgame.ClassIndex);
        Assert.Equal(["Paladin_Fortress", "Paladin_HolyShock_Aura"], endgame.SkillBar);

        var shield = endgame.Items[6];
        Assert.Equal("1HShield_Legendary_Generic_001", shield.ItemKey);
        Assert.Equal([1928803L, 1829560L], shield.Affixes.Select(a => a.Id));
        Assert.True(shield.Affixes[0].Greater);
        Assert.Equal([2611712L], shield.Tempered.Select(a => a.Id));
        Assert.Equal([0.08], shield.Affixes[1].Values!);
        Assert.Equal([2542658L], shield.Aspects.Select(a => a.Id));
        Assert.Equal([50.0], shield.Aspects[0].Values!);
        Assert.Equal(800, shield.Power);
        Assert.Equal(["Gem_Ruby_05"], shield.Sockets!);

        var step = Assert.Single(endgame.SkillSteps);
        Assert.Equal(new Dictionary<int, int> { [1849] = 15, [1659] = 1 }, step.RanksByNode); // unallocated nodes dropped
        Assert.Equal("Holy Light Aura is the main damage.\nUse Fortress on bosses.", endgame.SkillNotes);

        var boards = Assert.Single(endgame.ParagonSteps).Boards;
        Assert.Equal(["Paragon_Paladin_00", "Paragon_Paladin_06"], boards.Select(b => b.Id));
        Assert.Equal(new HashSet<int> { 10, 304 }, boards[0].Nodes);
        Assert.Equal(("Rare_107_Strength_Main", 150), (boards[0].Glyph, boards[0].GlyphLevel));
        Assert.Equal((3, 0, -1, (string?)null), (boards[1].Rotation, boards[1].X, boards[1].Y, boards[1].Glyph));
        Assert.Empty(plan.Variants[0].ParagonSteps); // no paragon in the profile
        Assert.Equal(["Warlock_ClassMechanic_Legion", "Warlock_ClassMechanic_Legion_B"], plan.Variants[0].ClassMechanics);
        Assert.Empty(endgame.ClassMechanics);
        var mercenaries = plan.Variants[0].Mercenaries!;
        Assert.Equal(("MercenaryClass_BerserkerCrone", "MercenaryClass_ShieldBearer", "NPC_Mercenary_ShieldBearer_Bastion", "trigger_skill"),
            (mercenaries.Hired, mercenaries.Reinforcement, mercenaries.ReinforcementSkill, mercenaries.Trigger));
        Assert.Equal([561], mercenaries.Picks); // 564, at 0, passed over
        Assert.Null(endgame.Mercenaries); // none in the profile
    }

    [Theory]
    [InlineData(0, 0, 0, 10, 0)]     // top gate of the start board
    [InlineData(1, 0, 0, 20, 10)]    // a quarter turn clockwise puts the top gate on the right
    [InlineData(3, 0, -1, 10, -1)]   // three turns put the left gate (index 210) at the bottom, touching the board below
    public void PlacesCellsOfRotatedBoards(int rotation, int boardX, int boardY, int x, int y)
    {
        var board = new PlacedBoard("b", rotation, boardX, boardY, null, 0, new HashSet<int>());

        Assert.Equal((x, y), board.Cell(rotation == 3 ? 210 : 10, 21));
    }

    [Fact]
    public void VariantFromLinkOverridesActiveVariant()
    {
        Assert.Equal(0, BuildPlanLoader.Parse(Profile(), variant: 0).DefaultVariant);
    }

    [Theory]
    [InlineData("https://maxroll.gg/d4/planner/jj4c9y0q#3", "jj4c9y0q", 2)]
    [InlineData("https://maxroll.gg/d4/planner/jj4c9y0q", "jj4c9y0q", null)]
    [InlineData("...\"link\":\"https://maxroll.gg/d4/planner/ab12cd34#1\"...", "ab12cd34", 0)] // inside a guide page
    public void ParsesPlannerLinks(string text, string id, int? variant)
    {
        Assert.Equal((id, variant), BuildPlanLoader.ParsePlannerLink(text));
    }

    [Fact]
    public void RejectsTextWithoutPlanner()
    {
        Assert.Null(BuildPlanLoader.ParsePlannerLink("https://maxroll.gg/d4/build-guides"));
    }
}
