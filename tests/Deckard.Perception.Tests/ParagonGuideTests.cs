using Deckard.Core.Builds;
using Deckard.Core.Items;
using Deckard.Perception.Paragon;

namespace Deckard.Perception.Tests;

public class ParagonGuideTests
{
    // 3x3 boards: every cell a node, the middle one a glyph socket.
    private static readonly ParagonData Paragon = new(
        new Dictionary<string, ParagonBoard>
        {
            ["A"] = new("Start", 3, ["n", "n", "n", "n", "s", "n", "n", "n", "n"]),
            ["B"] = new("Bee", 3, ["n", "n", "n", "n", "s", "n", "n", null, "n"]),
            ["C"] = new("Far", 3, ["n", "n", "n", "n", "s", "n", "n", "n", "n"]),
        },
        new Dictionary<string, ParagonNode>
        {
            ["n"] = new("Strength", "normal", ["+5 Strength"], 0, false),
            ["s"] = new("Glyph Socket", "socket", [], 0, false),
        },
        new Dictionary<string, ParagonGlyph> { ["G"] = new("Apostle", []) });

    private static readonly ParagonStep Step = new("Level 100", [
        new PlacedBoard("A", 0, 0, 0, "G", 100, new HashSet<int> { 1, 4 }),
        new PlacedBoard("C", 0, 5, 5, null, 0, new HashSet<int> { 0 }), // not next to anything attached
        new PlacedBoard("B", 1, 1, 0, null, 0, new HashSet<int> { 0 }),
    ]);

    // The start board attached, and the places of the two others in view and empty.
    private static readonly ParagonPlacement OnlyStart = new(0, 0, 10, Attached: 1, Empty: 0b110);

    [Fact]
    public void MarksTheBuildsNodesAndNamesTheGlyph()
    {
        var marks = ParagonGuide.Marks(OnlyStart, Step, Paragon);

        Assert.Contains(new GuideMark(10, 0, GuideKind.Take), marks);
        Assert.Contains(new GuideMark(10, 10, GuideKind.Socket, "Apostle (level 100)"), marks);
    }

    [Fact]
    public void OutlinesTheNextBoardWithItsTurn()
    {
        var marks = ParagonGuide.Marks(OnlyStart, Step, Paragon);

        Assert.Equal(2, ParagonGuide.NextBoard(OnlyStart, Step)); // the neighbour, not the first unattached one
        Assert.Equal(8 + 1, marks.Count(m => m.Kind == GuideKind.Ghost)); // its 8 node cells and the label
        // The middle cell stays put however the board is turned: board B at (1, 0) has it at cell (4, 1).
        Assert.Contains(new GuideMark(40, 10, GuideKind.Ghost, "Attach Bee here: press Rotate Board once"), marks);
    }

    [Fact]
    public void LeavesCoveredNodesUnmarked()
    {
        // Cell (1, 0) under a game tooltip; the socket (1, 1) reads as covered too, as an empty one always does.
        var marks = ParagonGuide.Marks(OnlyStart, Step, Paragon, (x, y) => (x, y) is (1, 0) or (1, 1));

        Assert.DoesNotContain(new GuideMark(10, 0, GuideKind.Take), marks);
        Assert.Contains(new GuideMark(10, 10, GuideKind.Socket, "Apostle (level 100)"), marks);
    }

    [Fact]
    public void LeavesTakenNodesUnmarked()
    {
        var marks = ParagonGuide.Marks(OnlyStart, Step, Paragon, taken: (x, y) => (x, y) is (1, 0) or (1, 1));

        Assert.DoesNotContain(new GuideMark(10, 0, GuideKind.Take), marks);
        Assert.Contains(new GuideMark(10, 10, GuideKind.Socket, "Apostle (level 100)"), marks); // the glyph still goes in
    }

    [Fact]
    public void NoHintWhereTheNextBoardsPlaceIsOutOfView()
    {
        // Bee's place scrolled off: it may well be attached already.
        var scrolled = OnlyStart with { Empty = 0b010 };

        Assert.Null(ParagonGuide.NextBoard(scrolled, Step));
        Assert.DoesNotContain(ParagonGuide.Marks(scrolled, Step, Paragon), m => m.Kind == GuideKind.Ghost);
    }

    [Fact]
    public void SaysWhichBoardToPick()
    {
        Assert.Equal("Pick Bee with the arrows, then Preview", ParagonGuide.PickHint(OnlyStart, Step, Paragon));
        // Bee's place out of view: still the build's next neighbour to pick.
        Assert.Equal("Pick Bee with the arrows, then Preview", ParagonGuide.PickHint(OnlyStart with { Empty = 0 }, Step, Paragon));
        Assert.Null(ParagonGuide.PickHint(OnlyStart with { Attached = 0b111 }, Step, Paragon));
    }

    [Fact]
    public void NoNextBoardOnceTheNeighboursAreAttached()
    {
        var all = OnlyStart with { Attached = 0b111 };

        Assert.Null(ParagonGuide.NextBoard(all, Step));
        Assert.DoesNotContain(ParagonGuide.Marks(all, Step, Paragon), m => m.Kind == GuideKind.Ghost);
    }

    [Fact]
    public void ListsTheBoardsInOrder()
    {
        Assert.Equal([
            "1. Start: 2 points; Apostle (level 100) in its socket.",
            "2. Far, don't rotate it: 1 point.",
            "3. Bee, attached right of Start, press Rotate Board once: 1 point.",
            "4 points in all.",
        ], ParagonGuide.Checklist(Step, Paragon));
    }

    [Theory]
    [InlineData(0, "don't rotate it")]
    [InlineData(1, "press Rotate Board once")]
    [InlineData(2, "press Rotate Board twice")]
    [InlineData(3, "press Rotate Board 3 times")]
    [InlineData(-1, "press Rotate Board 3 times")]
    public void SaysHowToTurnABoard(int rotation, string expected) => Assert.Equal(expected, ParagonGuide.Turns(rotation));
}
