using Deckard.Core.Builds;
using Deckard.Core.Items;

namespace Deckard.Core.Tests;

public class SkillPointsTests
{
    // A hub, a skill hanging from it through a junction, and a modifier hanging from the skill through another.
    private const int Hub = 1, Skill = 3, Modifier = 5;

    private static readonly SkillTree Tree = new(
        [
            new SkillNode(Hub, "cluster", "", "Basic", 0, 0, 0, 0, 0),
            new SkillNode(2, "junction", "", "", 0, 0, 0, 0, 0),
            new SkillNode(Skill, "skill", "Sorcerer_Spark", "Spark", 5, 1, 0, 0, 0),
            new SkillNode(4, "junction", "", "", 0, 0, 0, 0, 0),
            new SkillNode(Modifier, "modifier", "Sorcerer_Spark", "Spark: Flickering", 1, 2, 0, 0, 0),
        ],
        [new SkillLink(Hub, 2, []), new SkillLink(2, Skill, []), new SkillLink(Skill, 4, []), new SkillLink(4, Modifier, [])]);

    [Fact]
    public void TakesPointsOutwardFromTheHub()
    {
        var empty = new Dictionary<int, int>();
        Assert.True(SkillPoints.CanAdd(Tree, empty, Skill));
        Assert.False(SkillPoints.CanAdd(Tree, empty, Modifier)); // its skill isn't taken
        Assert.False(SkillPoints.CanAdd(Tree, empty, Hub));      // hubs and junctions take no points

        var withSkill = SkillPoints.Change(Tree, empty, Skill, add: true);
        Assert.Equal(new Dictionary<int, int> { [Skill] = 1 }, withSkill);
        Assert.True(SkillPoints.CanAdd(Tree, withSkill, Modifier));
    }

    [Fact]
    public void StopsAtTheRankLimit()
    {
        var full = new Dictionary<int, int> { [Skill] = 5, [Modifier] = 1 };
        Assert.False(SkillPoints.CanAdd(Tree, full, Skill));
        Assert.False(SkillPoints.CanAdd(Tree, full, Modifier));
        Assert.Equal(full, SkillPoints.Change(Tree, full, Skill, add: true)); // unchanged
    }

    [Fact]
    public void KeepsWhatHangsFromANode()
    {
        var ranks = new Dictionary<int, int> { [Skill] = 2, [Modifier] = 1 };
        Assert.True(SkillPoints.CanRemove(Tree, ranks, Skill)); // a point of two

        var one = SkillPoints.Change(Tree, ranks, Skill, add: false);
        Assert.Equal(1, one[Skill]);
        Assert.False(SkillPoints.CanRemove(Tree, one, Skill)); // the modifier hangs from its last point
        Assert.True(SkillPoints.CanRemove(Tree, one, Modifier));

        var noModifier = SkillPoints.Change(Tree, one, Modifier, add: false);
        Assert.Equal(new Dictionary<int, int> { [Skill] = 1 }, noModifier);
        Assert.Empty(SkillPoints.Change(Tree, noModifier, Skill, add: false));
        Assert.False(SkillPoints.CanRemove(Tree, new Dictionary<int, int>(), Skill));
    }

    // A leveling guide like the Warlock's: an early step, a step finishing one skill, then 14 points into another.
    private const int Early = 10, Core = 11, Ultimate = 12;

    private static readonly SkillStep[] Steps =
    [
        new("Basics", new Dictionary<int, int> { [Early] = 1, [Core] = 2 }),            // 3 points
        new("Finish Core", new Dictionary<int, int> { [Early] = 1, [Core] = 15 }),      // 16
        new("Ultimate", new Dictionary<int, int> { [Early] = 1, [Core] = 15, [Ultimate] = 15 }), // 31
    ];

    [Fact]
    public void ToSpend_MarksWhatTheStepAddsNotEverythingInIt()
    {
        // 16 spent, 3 available: the Ultimate step; the earlier nodes are covered by the points spent, unread off screen.
        var marks = SkillPoints.ToSpend(Steps, 2, spent: 16, shown: _ => null);
        Assert.Equal(new Dictionary<int, int> { [Ultimate] = 15 }, marks);
    }

    [Fact]
    public void ToSpend_KeepsTheStepWhenGearRanksMakeItLookDone()
    {
        // 16 spent and points to spend, but gear, charms or runes show the Ultimate at 15/15: the shown rank is bonus ranks.
        var marks = SkillPoints.ToSpend(Steps, 2, spent: 16, shown: node => node == Ultimate ? 15 : null);
        Assert.Equal(new Dictionary<int, int> { [Ultimate] = 15 }, marks);

        // Every point of the step spent: nothing left, however the ranks read.
        Assert.Empty(SkillPoints.ToSpend(Steps, 2, spent: 31, shown: node => node == Ultimate ? 15 : null));

        // Its tooltip gave its points (18/15, 3 from items: 15 spent): done, though the points spent fall short of the
        // step's (the player spent some elsewhere).
        Assert.Empty(SkillPoints.ToSpend(Steps, 2, spent: 29, shown: node => node == Ultimate ? 15 : null, exact: node => node == Ultimate));
    }

    [Fact]
    public void NextPoints_MarksOnlyWhatThePointsAvailableTake()
    {
        // 16 spent, 1 available, Core shown short at 4: that earlier node comes first, and one point is all there is.
        var next = SkillPoints.NextPoints(Steps, 2, spent: 16, available: 1, shown: node => node == Core ? 4 : null);
        Assert.Equal([(Core, 15, 1)], next);

        // 3 available, nothing short: the step's own, the Ultimate, takes them.
        Assert.Equal([(Ultimate, 15, 3)], SkillPoints.NextPoints(Steps, 2, spent: 16, available: 3, shown: _ => null));

        // No points to spend: nothing to mark.
        Assert.Empty(SkillPoints.NextPoints(Steps, 2, spent: 16, available: 0, shown: _ => null));
    }

    [Fact]
    public void ToSpend_AddsEarlierNodesTheTreeShowsShort()
    {
        // The player spent points elsewhere: the tree shows Core at 4, short of its 15.
        var marks = SkillPoints.ToSpend(Steps, 2, spent: 16, shown: node => node == Core ? 4 : null);
        Assert.Equal(new Dictionary<int, int> { [Ultimate] = 15, [Core] = 15 }, marks);

        // Points spent unknown (the docked panel): the step before is taken as covered.
        Assert.Equal(new Dictionary<int, int> { [Ultimate] = 15 }, SkillPoints.ToSpend(Steps, 2, spent: null, shown: _ => null));
    }
}
