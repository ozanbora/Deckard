using Deckard.Core.Capture;
using Deckard.Perception.Builds;

namespace Deckard.Perception.Tests;

public class PointsSpentTests
{
    [Theory]
    [InlineData("1 point spent", 1)]
    [InlineData("68 points spent", 68)]
    [InlineData("1,253 points spent", 1253)]
    [InlineData("Available Points", null)]
    [InlineData("points spent", null)]
    public void ReadsThePanelLine(string text, int? spent) => Assert.Equal(spent, PointsSpent.Parse(text));

    [Theory]
    [InlineData(0, 0)]   // nothing spent: the first step
    [InlineData(5, 1)]   // past the first step's 3: working toward the second's 7
    [InlineData(7, 2)]   // the second step done: the third
    [InlineData(81, 3)]  // everything spent: the last
    [InlineData(200, 3)]
    public void WorksTowardTheFirstStepNotYetSpent(int spent, int step) =>
        Assert.Equal(step, PointsSpent.StepFor([3, 7, 11, 81], spent));

    [Theory]
    [InlineData(35, 6, 9)] // a level 41 Warlock: step 7 (36) is one point away; 6 to spend reach into step 9 (52)
    [InlineData(35, 1, 7)]
    [InlineData(36, 2, 8)]
    [InlineData(66, 15, 11)]
    public void WorksTowardTheStepThatTakesEveryAvailablePoint(int spent, int available, int step) =>
        Assert.Equal(step, PointsSpent.StepFor([3, 7, 11, 17, 20, 25, 25, 36, 38, 52, 66, 81], spent, available));

    [Fact]
    public void ReadsThePointsToSpend()
    {
        Assert.Equal((35, 6), PointsSpent.Parse(["35 points spent", "Available Points", "• 6 0"]));
        Assert.Equal((35, 0), PointsSpent.Parse(["35 points spent", "Available Points"]));
        Assert.Null(PointsSpent.Parse(["Available Points", "6"]));
        Assert.Equal(6, PointsSpent.Available(["Available Points", "060"]));   // the icons read as 0s
        Assert.Equal(10, PointsSpent.Available(["Available Points", "o 10 0"]));
        Assert.Null(PointsSpent.Available(["Available Points", "odo"]));
        Assert.Null(PointsSpent.Available(["Available Points", "06"]));       // an icon lost: unsure
        Assert.Equal(1, PointsSpent.Available(["Available Points", "0I0"]));   // the font's 1 is a bare stroke
        Assert.Equal(1, PointsSpent.Available(["Available Points", "o | 0"]));
        Assert.Equal(1, PointsSpent.Available(["Available Points", ". 10"]));   // as read live in the docked panel: ◆ as ".", the 1, ◆ as 0
        Assert.Equal(12, PointsSpent.Available(["Available Points", "• 120"]));
        Assert.Equal(2, PointsSpent.Available(["Available Points", ".20"]));   // as read live: ◆ 2 ◆, once taken for 20
        Assert.Null(PointsSpent.Available(["Available Points", "10"]));        // ◆ 1 ◆ with the left icon lost: 1 or 10, unsure
    }

    [Fact]
    public void BlanksAllButTheCountBetweenItsIcons()
    {
        // The count line as measured in a 216x108 crop: icons at x 57-77 and 128-150, the 8 at 98-115, a panel edge at 210.
        using var count = Frame.Create(216, 108, TimeSpan.Zero);
        void Fill(int from, int to)
        {
            for (var y = 62; y < 82; y++)
                for (var x = from; x <= to; x++)
                    count.Pixels[y * count.Stride + x * Frame.BytesPerPixel + 2] = 200;
        }
        Fill(57, 77); Fill(98, 115); Fill(128, 150); Fill(210, 211);

        Assert.True(PointsSpent.BlankIcons(count));
        int Red(int x) => count.Pixels[70 * count.Stride + x * Frame.BytesPerPixel + 2];
        Assert.Equal(200, Red(105));                         // the count stays
        Assert.All(new[] { 60, 140, 210 }, x => Assert.True(Red(x) < 100)); // the icons and the edge go

        Assert.Equal(8, PointsSpent.Digits(["Available Points", "8"]));
        Assert.Equal(1, PointsSpent.Digits(["Available Points", "I"]));     // the font's 1
        Assert.Null(PointsSpent.Digits(["Available Points", "obo"]));
    }
}
