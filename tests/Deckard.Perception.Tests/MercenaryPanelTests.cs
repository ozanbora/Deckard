using System.Text;
using Deckard.Core.Builds;
using Deckard.Core.Items;
using Deckard.Perception.Mercenaries;

namespace Deckard.Perception.Tests;

public class MercenaryPanelTests
{
    // Two mercenaries as the season data has them, and the Warlock skill a guide calls the reinforcement on.
    private const string Catalog = """
        {
          "schemaVersion": 8, "game": "d4", "season": 15, "gameBuild": "3.2.1.73552", "locale": "enus", "classes": [], "entries": [],
          "skills": { "Warlock_BurningSkull": "Blazing Scream" },
          "mercenaries": {
            "MercenaryClass_BerserkerCrone": { "name": "Varyana", "nodes": { "562": "Mercenary_BerserkerCrone_Cleave" },
              "skills": { "Mercenary_BerserkerCrone_Cleave": { "name": "Cleave" } },
              "tree": { "links": [], "nodes": [
                { "node": 565, "kind": "cluster", "skill": "", "name": "", "maxRanks": 0, "requiredLevel": 0, "x": 0, "y": -1264, "icon": 0 },
                { "node": 562, "kind": "skill", "skill": "Mercenary_BerserkerCrone_Cleave", "name": "Cleave", "maxRanks": 1, "requiredLevel": 0, "x": -1250, "y": -670, "icon": 0 } ] } },
            "MercenaryClass_ShieldBearer": { "name": "Raheir", "nodes": {},
              "skills": { "NPC_Mercenary_ShieldBearer_Provoke": { "name": "Provoke" }, "NPC_Mercenary_ShieldBearer_Bastion": { "name": "Bastion" },
                "NPC_Mercenary_ShieldBearer_PassiveA1": { "name": "Raheir's Guard" } },
              "tree": { "links": [], "nodes": [
                { "node": 565, "kind": "cluster", "skill": "", "name": "", "maxRanks": 0, "requiredLevel": 0, "x": 0, "y": -1264, "icon": 0 },
                { "node": 567, "kind": "skill", "skill": "NPC_Mercenary_ShieldBearer_Bastion", "name": "Bastion", "maxRanks": 1, "requiredLevel": 0, "x": 624, "y": 735, "icon": 0 },
                { "node": 481, "kind": "skill", "skill": "NPC_Mercenary_ShieldBearer_Provoke", "name": "Provoke", "maxRanks": 1, "requiredLevel": 0, "x": -1880, "y": 600, "icon": 0 } ] } },
            "MercenaryClass_BountyHunter": { "name": "Subo", "nodes": {}, "skills": {} }
          }
        }
        """;

    private static readonly AffixCatalog Data = AffixCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(Catalog)));

    // The leveling Warlock's guide: Varyana with Cleave, Raheir's Bastion on Blazing Scream.
    private static readonly PlannedMercenaries Guide = new("MercenaryClass_BerserkerCrone", [562], "MercenaryClass_ShieldBearer",
        "NPC_Mercenary_ShieldBearer_Bastion", "Warlock_BurningSkull", "");

    [Fact]
    public void ReadsTheTabAndSaysWhatToChange()
    {
        // As OCR read the tab live: Subo hired, no reinforcement.
        var seen = MercenaryPanel.Parse(["MERCENARIES", "HIRED MERCE", "Active: Subo | Ra", "NO REINFORCEMENT ENLISTED", "Visit The Den to Enlist a Reinforcement."], Data);

        Assert.Equal(new MercenarySighting("MercenaryClass_BountyHunter", null), seen);
        Assert.Equal(("Hire Varyana in The Den instead of Subo: the guide's mercenary.",
            "Enlist Raheir as reinforcement in The Den: Bastion (unlocks with Rapport if not yet), called by Blazing Scream."), MercenaryPanel.Advise(seen!, Guide, Data));
    }

    [Fact]
    public void ConfirmsTheGuidesPicks()
    {
        var advice = MercenaryPanel.Advise(new MercenarySighting("MercenaryClass_BerserkerCrone", "MercenaryClass_ShieldBearer"), Guide, Data);

        Assert.Equal(("Varyana is the guide's mercenary: pick Cleave.", "Raheir is the guide's reinforcement: Bastion (unlocks with Rapport if not yet), called by Blazing Scream."), advice);
        Assert.Null(MercenaryPanel.Parse(["SKILL TREE", "Available Points"], Data)); // another tab
    }

    [Fact]
    public void PlacesTheGuidesPicksOnAMercenarysOpenTree()
    {
        // Varyana's own panel on its Skills tab, as OCR reads it; Cleave sits at (2816, 594) in the 3440x1440 frame.
        var seen = MercenaryPanel.Parse(["VAR YANA", "Skills Rapport", "Available Points"], Data);

        Assert.Equal("MercenaryClass_BerserkerCrone", seen?.Tree);
        var (x, y, order, name) = Assert.Single(MercenaryPanel.TreePicks(seen!.Tree!, Guide, Data, 3440, 1440));
        Assert.Equal((2818, 600, 1, "Cleave"), ((int)Math.Round(x), (int)Math.Round(y), order, name));
        Assert.Empty(MercenaryPanel.TreePicks("MercenaryClass_BountyHunter", Guide, Data, 3440, 1440)); // not the guide's
    }

    [Fact]
    public void MarksTheGuidesSkillInAReinforcementsRow()
    {
        // Raheir's own panel as OCR reads it: no tree, a row of his skills in the data's order (here Provoke, Bastion).
        var seen = MercenaryPanel.Parse(["RAHE IR", "Reinforcement Rapport", "Reinforcements will emerge during combat and execute their Skill", "SELECT A SKILL"], Data);

        Assert.Equal(new MercenarySighting(null, null, "MercenaryClass_ShieldBearer", SkillRow: true), seen);
        var (x, y, _, name) = Assert.Single(MercenaryPanel.TreePicks(seen!.Tree!, Guide, Data, 3440, 1440, seen.SkillRow));
        Assert.Equal((2656 + 120, 1010, "Bastion"), ((int)Math.Round(x), (int)Math.Round(y), name)); // the second of the row
    }
}