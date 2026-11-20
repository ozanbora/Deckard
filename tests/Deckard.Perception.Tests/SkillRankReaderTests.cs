using Deckard.Perception.SkillTrees;

namespace Deckard.Perception.Tests;

public class SkillRankReaderTests
{
    [Theory]
    [InlineData("4/15", 15, 4)]
    [InlineData("10/15", 15, 10)]
    [InlineData(" 0 / 15 ", 15, 0)]
    [InlineData("1/1", 1, 1)]
    [InlineData("1/13", 15, null)]   // OCR's "15" misread: not this skill's maximum
    [InlineData("0/16", 15, null)]
    [InlineData("0/1é", 15, null)]
    [InlineData("ill 5", 15, null)]
    [InlineData("(VIS", 15, null)]
    [InlineData("16/15", 15, 16)]    // items add ranks over the limit points stop at
    [InlineData("18/15", 15, 18)]
    public void ReadsOnlyARankThatFitsTheSkill(string text, int maxRanks, int? rank) =>
        Assert.Equal(rank, SkillRankReader.Parse(text, maxRanks));

    [Fact]
    public void ReadsItemRanksOffTheSkillTooltip()
    {
        // As OCR reads Apocalypse's tooltip with 13 points spent and 3 ranks from items (3440x1440).
        string[] lines = ["Apocalypse", "RANK 16/15", "(Item Contribution: 3)", "Ultimate Occult Hellfire", "Stacks Cost: 20, up to 100"];
        Assert.Equal((16, 3, 2), SkillRankReader.Tooltip(lines, "Apocalypse", 15));

        // As read at 1x: the words garbled and the blue number dropped, so the line is to be read again.
        Assert.Equal((16, null, 2), SkillRankReader.Tooltip(["Apocalypse", "RANK 16/15", "(Item Contnbuuon:", "Ultimate"], "Apocalypse", 15));

        // No items adding ranks: no Contribution line.
        Assert.Equal((4, 0, 1), SkillRankReader.Tooltip(["Rampage", "RANK 4/15", "Core Demonic"], "Rampage", 15));

        // Another skill's tooltip, or a rank OCR misread.
        Assert.Null(SkillRankReader.Tooltip(lines, "Rampage", 15));
        Assert.Null(SkillRankReader.Tooltip(["Apocalypse", "RANK 16/13", "(Item Contribution: 3)"], "Apocalypse", 15));
    }

    [Fact]
    public void ATakenModifierHasItsLinkLitRed()
    {
        // A modifier's diamond (half 18 px) on a dark tree, its link coming in from below: red when taken, grey when not.
        using var taken = Deckard.Perception.Tests.TestScenes.Scene(200, 200);
        using var untaken = Deckard.Perception.Tests.TestScenes.Scene(200, 200);
        foreach (var (frame, link) in new[] { (taken, ((byte)150, (byte)20, (byte)20)), (untaken, ((byte)90, (byte)90, (byte)85)) })
        {
            Deckard.Perception.Tests.TestScenes.Fill(frame, new(0, 0, 200, 200), (20, 18, 16));
            Deckard.Perception.Tests.TestScenes.Fill(frame, new(97, 100, 6, 100), link);
            Deckard.Perception.Tests.TestScenes.Disc(frame, 100, 100, 14, (120, 120, 120));
        }

        Assert.True(SkillRankReader.TakenAt(taken, 100, 100, 18));
        Assert.False(SkillRankReader.TakenAt(untaken, 100, 100, 18));
    }
}
