using Deckard.Core.Capture;
using Deckard.Perception.Tooltips;
using static Deckard.Perception.Tests.TestScenes;

namespace Deckard.Perception.Tests;

public class TooltipWatcherTests
{
    private static readonly PixelRect Equipped = new(2301, 165, 490, 900);

    private static Frame WithTooltip(PixelRect tooltip, int seed)
    {
        var frame = Scene(3440, 1440, tooltip);
        Lines(frame, tooltip, 12, seed);
        return frame;
    }

    private static Frame Empty() => Scene(3440, 1440);

    [Fact]
    public void ReportsATooltipOnceAfterItSettles()
    {
        var watcher = new TooltipWatcher(new TooltipLocator());
        using var item = WithTooltip(Equipped, seed: 1);

        Assert.Empty(watcher.Check(item));       // first sighting: wait for it to settle
        Assert.Single(watcher.Check(item));      // same look twice: report
        Assert.Empty(watcher.Check(item));       // still hovering: stay quiet
        Assert.Empty(watcher.Check(item));
    }

    /// <summary>
    /// Beside a comparison the lit floor's streaks frame a box whose edges jump 60 px from check to check (measured at
    /// 3440x1440): the tooltips are reported all the same, once, and the card about them isn't hidden as it jumps.
    /// </summary>
    [Fact]
    public void AJumpingBoxBesideTheTooltipsDoesntHoldThemUp()
    {
        var watcher = new TooltipWatcher(new TooltipLocator());
        Frame At(int y, int height)
        {
            var frame = Scene(3440, 1440, Equipped, new PixelRect(800, y, 500, height));
            Lines(frame, Equipped, 12, seed: 1);
            return frame;
        }
        using var up = At(140, 560);
        using var down = At(200, 680);

        Assert.Empty(watcher.Check(up));
        Assert.Empty(watcher.Check(down)); // the floor box jumped: known as such from now on
        var reported = watcher.Check(up);  // the item settled
        Assert.Contains(reported, r => r.X >= 2290 && r.X <= 2310);
        Assert.DoesNotContain(reported, r => r.X < 1400);
        foreach (var frame in new[] { up, down, up, down })
        {
            Assert.Empty(watcher.Check(frame));
            Assert.False(watcher.Replaced);
        }
    }

    [Fact]
    public void ReportsADifferentItem()
    {
        var watcher = new TooltipWatcher(new TooltipLocator());
        using var first = WithTooltip(Equipped, seed: 1);
        using var second = WithTooltip(Equipped, seed: 2); // same place, different text

        watcher.Check(first);
        Assert.Single(watcher.Check(first));
        Assert.Empty(watcher.Check(second));
        Assert.Single(watcher.Check(second));
    }

    [Fact]
    public void SaysWhenAnotherItemReplacesTheOneReported()
    {
        var watcher = new TooltipWatcher(new TooltipLocator());
        using var first = WithTooltip(Equipped, seed: 1);
        using var second = WithTooltip(Equipped, seed: 2);
        using var none = Empty();

        watcher.Check(first);
        watcher.Check(first);
        Assert.False(watcher.Replaced);
        watcher.Check(second);            // first sight of another item: the card about the first is out of date
        Assert.True(watcher.Replaced);
        Assert.Single(watcher.Check(second));
        Assert.False(watcher.Replaced);   // once, not on every check

        watcher.Check(none);              // closing a tooltip isn't replacing it: the player may be on the card
        Assert.False(watcher.Replaced);
    }

    [Fact]
    public void ReportsTheItemAgainWhenItsTooltipComesBack()
    {
        var watcher = new TooltipWatcher(new TooltipLocator());
        using var item = WithTooltip(Equipped, seed: 1);
        using var advanced = WithTooltip(Equipped, seed: 2); // Alt tapped: the advanced view, for one check

        watcher.Check(item);
        Assert.Single(watcher.Check(item));
        Assert.Empty(watcher.Check(advanced));
        Assert.True(watcher.Replaced);         // the card hides
        Assert.Single(watcher.Check(item));    // back: read again, so the card shows again
        Assert.Empty(watcher.Check(item));
    }

    [Fact]
    public void ReportsTheSameItemAgainAfterTheTooltipCloses()
    {
        var watcher = new TooltipWatcher(new TooltipLocator());
        using var item = WithTooltip(Equipped, seed: 1);
        using var none = Empty();

        watcher.Check(item);
        Assert.Single(watcher.Check(item));
        Assert.Empty(watcher.Check(none));
        Assert.Empty(watcher.Check(item));
        Assert.Single(watcher.Check(item));
    }

    [Fact]
    public void KeepsQuietWhileTheLocatedEdgeWobbles()
    {
        var watcher = new TooltipWatcher(new TooltipLocator());
        using var item = WithTooltip(Equipped, seed: 1);
        using var wobbled = WithTooltip(Equipped with { X = Equipped.X + 10, Width = Equipped.Width - 10 }, seed: 1);

        watcher.Check(item);
        Assert.Single(watcher.Check(item));
        Assert.Empty(watcher.Check(wobbled));
        Assert.False(watcher.Replaced);
        Assert.Empty(watcher.Check(item));
    }

    [Fact]
    public void LeavesOutABoxThatHeldNoTooltip()
    {
        var watcher = new TooltipWatcher(new TooltipLocator());
        var world = new PixelRect(460, 500, 560, 780); // the game's world, located as a tooltip with jumping edges
        using var both = Scene(3440, 1440, world, Equipped);
        Lines(both, Equipped, 12, seed: 1);
        using var jumped = Scene(3440, 1440, world with { X = 574, Width = 446 }, Equipped);
        Lines(jumped, Equipped, 12, seed: 1);

        watcher.Check(both);
        Assert.Equal(2, watcher.Check(both).Count);
        watcher.Ignore(world);
        Assert.Empty(watcher.Check(jumped));
        Assert.False(watcher.Replaced);
    }

    [Fact]
    public void ALocatedEdgeThatFlipsAFewPixelsIsTheSameTooltip()
    {
        var watcher = new TooltipWatcher(new TooltipLocator());
        using var item = WithTooltip(Equipped, seed: 1);
        using var flipped = WithTooltip(Equipped, seed: 1);
        Fill(flipped, new PixelRect(Equipped.X - 6, Equipped.Y, 2, Equipped.Height), Bronze); // a second border line: the edge is found 6 px out
        Assert.NotEqual(new TooltipLocator().Locate(item)[0].X, new TooltipLocator().Locate(flipped)[0].X);

        watcher.Check(item);
        Assert.Single(watcher.Check(item));
        Assert.Empty(watcher.Check(flipped));
        Assert.False(watcher.Replaced);
        Assert.Empty(watcher.Check(flipped));
        Assert.Empty(watcher.Check(item));
    }

    [Fact]
    public void AnotherBoxBesideTheReportedTooltipReplacesNothing()
    {
        var watcher = new TooltipWatcher(new TooltipLocator());
        using var item = WithTooltip(Equipped, seed: 1);
        using var withWorld = Scene(3440, 1440, new PixelRect(460, 500, 560, 780), Equipped); // the world, framed for a moment
        Lines(withWorld, Equipped, 12, seed: 1);

        watcher.Check(item);
        Assert.Single(watcher.Check(item));
        Assert.Empty(watcher.Check(withWorld));
        Assert.False(watcher.Replaced);                        // the card about the item stays up
        Assert.Equal(2, watcher.Check(withWorld).Count);       // read once it settles
        Assert.False(watcher.Replaced);
    }

    [Fact]
    public void ReportsAMovedTooltip()
    {
        var watcher = new TooltipWatcher(new TooltipLocator());
        using var here = WithTooltip(Equipped, seed: 1);
        using var there = WithTooltip(Equipped with { X = 2772 }, seed: 1);

        watcher.Check(here);
        Assert.Single(watcher.Check(here));
        Assert.Empty(watcher.Check(there));
        Assert.Single(watcher.Check(there));
    }

    [Fact]
    public void WaitsOutAFadeIn()
    {
        var watcher = new TooltipWatcher(new TooltipLocator());
        // Mid fade-in the panel is already there but its text is still dim.
        using var fading = Scene(3440, 1440, Equipped);
        Lines(fading, Equipped, 12, seed: 1, color: (70, 70, 66));
        using var settled = WithTooltip(Equipped, seed: 1);

        Assert.Empty(watcher.Check(fading));
        Assert.Empty(watcher.Check(settled));
        Assert.Single(watcher.Check(settled));
    }
}
