using Deckard.Core.Capture;
using Deckard.Perception.Tooltips;
using static Deckard.Perception.Tests.TestScenes;

namespace Deckard.Perception.Tests;

/// <summary>Icons as measured on 1080p tooltips (367 px wide): bright blobs left of the affix text.</summary>
public class AffixIconTests
{
    private static readonly PixelRect Tooltip = new(100, 50, 367, 700);
    private static readonly PixelRect Line = new(140, 300, 250, 24); // the text starts 40 px in

    [Theory]
    [InlineData(13, 13, true)]   // greater affix star
    [InlineData(12, 12, true)]
    [InlineData(7, 9, false)]    // regular affix diamond
    [InlineData(9, 15, false)]   // fixed affix shield
    [InlineData(23, 23, false)]  // socketed gem
    [InlineData(0, 0, false)]    // no icon
    public void TellsTheGreaterAffixStar(int width, int height, bool greater)
    {
        using var frame = Scene(800, 800);
        Fill(frame, new PixelRect(0, 0, 800, 800), (20, 18, 14));
        if (width > 0)
            Fill(frame, new PixelRect(Tooltip.X + 15, Line.Y + 4, width, height), (230, 190, 170));

        Assert.Equal(greater, AffixIcon.IsGreater(frame, Tooltip, Line));
    }

    [Fact]
    public void TextRunningThroughTheColumnIsNoIcon()
    {
        using var frame = Scene(800, 800);
        Fill(frame, new PixelRect(0, 0, 800, 800), (20, 18, 14));
        // "2,625 Armor": a base line with no icon, its text starting where icons sit.
        Fill(frame, new PixelRect(Tooltip.X + 16, Line.Y + 4, 120, 13), (220, 220, 210));

        Assert.False(AffixIcon.IsGreater(frame, Tooltip, Line with { X = Tooltip.X + 16 }));
    }
}
