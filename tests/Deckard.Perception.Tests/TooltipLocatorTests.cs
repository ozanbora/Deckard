using Deckard.Core.Capture;
using Deckard.Perception.Tooltips;
using static Deckard.Perception.Tests.TestScenes;

namespace Deckard.Perception.Tests;

public class TooltipLocatorTests
{
    [Fact]
    public void Finds_OneTooltipPerFrameDespiteDoubleBorder()
    {
        using var frame = Scene(3440, 1440, new PixelRect(2301, 165, 490, 900));

        var found = Assert.Single(new TooltipLocator().Locate(frame));

        Assert.InRange(found.X, 2299, 2303);
        Assert.InRange(found.Width, 486, 494);
        Assert.True(found.Y < 165, "top is padded upward to include the header");
        Assert.True(found.Bottom > 1065, "bottom is padded to include the footer");
    }

    [Fact]
    public void Finds_SideBySideComparisonTooltips()
    {
        using var frame = Scene(3440, 1440, new PixelRect(1700, 200, 490, 800), new PixelRect(2250, 200, 490, 850));

        var found = new TooltipLocator().Locate(frame);

        Assert.Equal(2, found.Count);
        Assert.InRange(found[0].X, 1698, 1702);
        Assert.InRange(found[1].X, 2248, 2252);
    }

    /// <summary>
    /// A comparison under a warm-lit floor (measured on a 3440x1440 capture): the floor's bronze streaks above and left of
    /// the tooltips pair with their borders, and merging every overlap made one box 808 px wide, two tooltips read as
    /// one. No box is wider than a tooltip can be, and the hovered one stays a box of its own.
    /// </summary>
    [Fact]
    public void Comparison_UnderAWarmFloor_StaysTwoTooltips()
    {
        var worn = new PixelRect(1708, 290, 520, 580);
        var hovered = new PixelRect(2248, 330, 520, 960);
        using var frame = Scene(3440, 1440, worn, hovered);
        Fill(frame, new PixelRect(1630, 0, 830, 288), Body);
        for (var x = 1640; x < 1708; x += 12)
            Fill(frame, new PixelRect(x, 0, 2, 550), Bronze);
        for (var x = 1730; x < 2446; x += 12)
            Fill(frame, new PixelRect(x, 0, 2, 230), Bronze);
        Fill(frame, new PixelRect(2240, 0, 2, 790), Bronze);

        var found = new TooltipLocator().Locate(frame);

        Assert.All(found, r => Assert.True(r.Width <= 1440 * 0.42 + 2, $"{r} is wider than a tooltip"));
        Assert.Contains(found, r => r.X >= worn.X - 80 && r.Right <= worn.Right + 16);
        Assert.Contains(found, r => r.X >= hovered.X - 40 && r.X <= hovered.X + 2 && r.Right >= hovered.Right - 6);
    }

    /// <summary>
    /// Other screens: the tooltip is 491 px wide at 1440p (0.341 of the height) and 900 px tall, and keeps those
    /// proportions, two thirds of the way across the screen.
    /// </summary>
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    [InlineData(1920, 1200)]
    public void ScalesWithResolution(int width, int height)
    {
        var s = height / 1440.0;
        var tooltip = new PixelRect(width * 2 / 3, (int)(165 * s), (int)(491 * s), (int)(900 * s));
        using var frame = Scene(width, height, tooltip);

        var found = Assert.Single(new TooltipLocator().Locate(frame));
        Assert.InRange(found.X, tooltip.X - 2, tooltip.X + 2);
        Assert.InRange(found.Width, tooltip.Width - 4, tooltip.Width + 4);
    }

    [Fact]
    public void Ignores_ShortBronzeFramesAndPlainScenes()
    {
        // Inventory slot frames are bronze too, but much shorter than a tooltip border.
        using var slots = Scene(3440, 1440, new PixelRect(1000, 600, 490, 120));
        using var empty = Scene(3440, 1440);

        Assert.Empty(new TooltipLocator().Locate(slots));
        Assert.Empty(new TooltipLocator().Locate(empty));
    }

    [Theory]
    [InlineData(112, 97, 59, true)]  // border, measured
    [InlineData(101, 81, 49, true)]
    [InlineData(28, 21, 13, false)]  // tooltip body
    [InlineData(90, 92, 85, false)]  // grey stone
    [InlineData(200, 90, 30, false)] // legendary orange header
    public void BorderColor(byte r, byte g, byte b, bool expected)
    {
        Assert.Equal(expected, TooltipLocator.IsBorderColor(r, g, b));
    }
}
