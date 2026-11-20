using Deckard.Core.Capture;
using Deckard.Perception;
using Deckard.Perception.Paragon;
using static Deckard.Perception.Tests.TestScenes;

namespace Deckard.Perception.Tests;

public class ParagonLocatorTests
{
    // Two boards side by side, each an arrow-like pattern of node cells (asymmetric top to bottom, like the start board).
    private static readonly (int X, int Y)[] Start =
        [.. from y in Enumerable.Range(0, 15) from x in Enumerable.Range(0, 21) where Math.Abs(x - 10) <= Math.Min(y, 14 - y) / 2 + (y % 3 == 0 ? 1 : 0) select (x, y)];
    private static readonly (int X, int Y)[] Next = [.. Start.Select(c => (c.X + 21, c.Y))];

    private static readonly PixelRect FullTab = new(1745, 42, 110, 38), DockedTab = new(2995, 126, 110, 44);

    /// <summary>The paragon screen with the given cells drawn as grey node symbols on a dark background.</summary>
    private static Frame Screen(IEnumerable<(int X, int Y)> nodes, double originX, double originY, double pitch, PixelRect? tab = null)
    {
        var frame = Scene(3440, 1440);
        Fill(frame, new PixelRect(0, 0, 3440, 1440), (20, 18, 16));
        Fill(frame, tab ?? FullTab, (160, 20, 20)); // the lit "Paragon" tab
        foreach (var (x, y) in nodes)
            Disc(frame, originX + x * pitch, originY + y * pitch, pitch * 0.18, (97, 94, 97));
        return frame;
    }

    [Theory]
    [InlineData(1248.7, 300.5, 44.8)]  // default zoom
    [InlineData(1101.1, 170.7, 73.7)]  // zoomed in and panned, the start board's bottom off screen
    public void FindsTheStartBoard(double originX, double originY, double pitch)
    {
        using var frame = Screen(Start, originX, originY, pitch);

        var placement = new ParagonLocator([Start, Next]).Locate(frame);

        Assert.NotNull(placement);
        Assert.InRange(placement.Value.Pitch, pitch * 0.98, pitch * 1.02);
        var (x, y) = placement.Value.ToScreen(10, 7);
        Assert.InRange(x, originX + 10 * pitch - 4, originX + 10 * pitch + 4);
        Assert.InRange(y, originY + 7 * pitch - 4, originY + 7 * pitch + 4);
        Assert.True(placement.Value.IsAttached(0));
        Assert.False(placement.Value.IsAttached(1)); // in view but not drawn: not attached yet
        Assert.True(placement.Value.IsEmpty(1));
    }

    [Fact]
    public void AnAttachedBoardStaysAttachedScrolledOutOfView()
    {
        const double pitch = 44.8;
        var locator = new ParagonLocator([Start, Next]);
        using (var both = Screen(Start.Concat(Next), 700, 300, pitch))
            Assert.True(locator.Locate(both)!.Value.IsAttached(1));
        // Panned until the start board is off the screen to the left: only the second board shows.
        using var frame = Screen(Start.Concat(Next), 700 - 21 * pitch - 400, 300, pitch);

        var placement = locator.Locate(frame);

        Assert.NotNull(placement);
        Assert.True(placement.Value.IsAttached(0));
        Assert.False(placement.Value.IsEmpty(0));
    }

    [Fact]
    public void FollowsADragUpAndBack()
    {
        // A dense board, still fitting well when one row off. Moving over half a cell between two analysed frames (a
        // fast drag zoomed in) once made the tracking round to the neighbouring row and stay there.
        (int X, int Y)[] dense = [.. from y in Enumerable.Range(0, 15) from x in Enumerable.Range(3, 15) select (x, y)];
        const double pitch = 60, x0 = 900, y0 = 250;
        var locator = new ParagonLocator([dense]);
        foreach (var dy in new[] { 0.0, -0.6 * pitch, -1.2 * pitch, -0.6 * pitch, 0 })
        {
            using var frame = Screen(dense, x0, y0 + dy, pitch);

            var placement = locator.Locate(frame);

            Assert.NotNull(placement);
            var (_, y) = placement.Value.ToScreen(10, 7);
            Assert.InRange(y, y0 + dy + 7 * pitch - 4, y0 + dy + 7 * pitch + 4);
        }
    }

    [Fact]
    public void SeesAnAttachedBoard()
    {
        using var frame = Screen(Start.Concat(Next), 700, 300, 44.8);

        var placement = new ParagonLocator([Start, Next]).Locate(frame);

        Assert.NotNull(placement);
        Assert.True(placement.Value.IsAttached(0) && placement.Value.IsAttached(1));
    }

    [Fact]
    public void AFewLitCellsDontMakeABoardAttached()
    {
        // Only two cells of the next board in view, both on something light (once the game's search box text).
        const double x0 = 1248.7, y0 = 300.5, pitch = 44.8;
        var corner = Next.OrderBy(c => c.X).ThenBy(c => c.Y).Take(2).ToArray();
        using var frame = Screen(Start.Concat(corner), x0, y0, pitch);

        var placement = new ParagonLocator([Start, corner]).Locate(frame);

        Assert.NotNull(placement);
        Assert.False(placement.Value.IsAttached(1));
    }

    [Fact]
    public void KeepsTheStartBoardDraggedNearlyOffScreen()
    {
        const double x0 = 1248.7, y0 = 300.5, pitch = 44.8;
        var locator = new ParagonLocator([Start, Next]);
        using (var first = Screen(Start, x0, y0, pitch))
            Assert.NotNull(locator.Locate(first));
        // Dragged up until only its bottom three rows are left below the top bar: a few cells, still the start board.
        var up = 0.07 * 1440 + 10 - 12 * pitch;
        using var frame = Screen(Start, x0, up, pitch);

        var placement = locator.Locate(frame);

        Assert.NotNull(placement);
        Assert.True(placement.Value.IsAttached(0));
        var (_, y) = placement.Value.ToScreen(10, 14);
        Assert.InRange(y, up + 14 * pitch - 4, up + 14 * pitch + 4);
    }

    [Fact]
    public void FindsTheBoardDockedInTheAbilitiesPanel()
    {
        // Measured on a docked capture: the start node (10, 14) at about (2976, 724), 84 px a cell.
        const double pitch = 84.4, x0 = 2132.2, y0 = -457.3;
        using var frame = Screen(Start, x0, y0, pitch, DockedTab);

        var placement = new ParagonLocator([Start, Next]).Locate(frame);

        Assert.NotNull(placement);
        Assert.Equal(ScreenLayout.Docked, placement.Value.Layout);
        var (x, y) = placement.Value.ToScreen(10, 12);
        Assert.InRange(x, x0 + 10 * pitch - 4, x0 + 10 * pitch + 4);
        Assert.InRange(y, y0 + 12 * pitch - 4, y0 + 12 * pitch + 4);
    }

    [Fact]
    public void TheCharacterPanelsRedBackdropIsNoParagonTab()
    {
        // The Character panel: behind the doll, red from x 2920 to 3300 and y 100 to 880, over the docked tab's place.
        using var frame = Screen(Start, 2132.2, -457.3, 84.4, new PixelRect(2920, 100, 380, 780));

        Assert.Null(ParagonLocator.Layout(frame));
        Assert.Null(new ParagonLocator([Start, Next]).Locate(frame));
    }

    [Fact]
    public void HoldsTheGridUnderAGameTooltip()
    {
        const double x0 = 1248.7, y0 = 300.5, pitch = 44.8;
        var locator = new ParagonLocator([Start, Next]);
        using (var first = Screen(Start, x0, y0, pitch))
            Assert.NotNull(locator.Locate(first));
        // A tooltip panel over the board's upper half, like a node's in game: nearly black, a thin grey-green border and a
        // separator, a title, a stat line and a button.
        using var frame = Screen(Start, x0, y0, pitch);
        Fill(frame, new PixelRect(1150, 280, 700, 330), (10, 11, 10));
        foreach (var edge in new[] { 280, 380, 606 })
            Fill(frame, new PixelRect(1150, edge, 700, 4), (67, 84, 80));
        foreach (var side in new[] { 1150, 1846 })
            Fill(frame, new PixelRect(side, 280, 4, 330), (67, 84, 80));
        foreach (var (top, width) in new[] { (330, 220), (430, 260), (570, 120) })
            Fill(frame, new PixelRect(1500 - width / 2, top, width, 14), (200, 196, 190));

        var placement = locator.Locate(frame);

        Assert.NotNull(placement);
        var (x, y) = placement.Value.ToScreen(10, 12);
        Assert.InRange(x, x0 + 10 * pitch - 4, x0 + 10 * pitch + 4);
        Assert.InRange(y, y0 + 12 * pitch - 4, y0 + 12 * pitch + 4);
        Assert.True(locator.IsCovered(10, 2)); // under the panel: no mark there
        Assert.True(locator.IsCovered(10, 3)); // under its stat line, which reads light like a node
        Assert.False(locator.IsCovered(10, 12)); // below it
        Assert.Contains(locator.Masked, r => r.X <= 1160 && r.Right >= 1840 && r.Y <= 290 && r.Bottom >= 600);
    }

    [Fact]
    public void ALaterBoardSeenAttachedMeansTheEarlierOnesAre()
    {
        // Zoomed out: the start board, and below it the build's third board; the second (above) is out of view.
        const double pitch = 30, x0 = 1400, y0 = 300;
        (int X, int Y)[] above = [.. Start.Select(c => (c.X, c.Y - 21))], below = [.. Start.Select(c => (c.X, c.Y + 21))];
        using var frame = Screen(Start.Concat(below), x0, y0, pitch);

        var placement = new ParagonLocator([Start, above, below]).Locate(frame);

        Assert.NotNull(placement);
        Assert.True(placement.Value.IsAttached(2));
        Assert.True(placement.Value.IsAttached(1)); // never seen, but attached before the third
    }

    [Fact]
    public void ADetachedBoardIsNoLongerAttached()
    {
        const double pitch = 44.8;
        var locator = new ParagonLocator([Start, Next]);
        using (var both = Screen(Start.Concat(Next), 700, 300, pitch))
            Assert.True(locator.Locate(both)!.Value.IsAttached(1));
        // The second board detached: its place in view and dark.
        using var frame = Screen(Start, 700, 300, pitch);

        var placement = locator.Locate(frame);

        Assert.NotNull(placement);
        Assert.False(placement.Value.IsAttached(1));
        Assert.True(placement.Value.IsEmpty(1));
        Assert.Equal(1, locator.SeenAttached);
    }

    [Fact]
    public void NeedsTheParagonTab()
    {
        using var frame = Screen(Start, 1248.7, 300.5, 44.8);
        Fill(frame, new PixelRect(1745, 42, 110, 38), (60, 60, 60)); // another screen

        Assert.Null(new ParagonLocator([Start, Next]).Locate(frame));
    }
}
