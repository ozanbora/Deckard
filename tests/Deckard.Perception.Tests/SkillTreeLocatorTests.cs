using Deckard.Core.Capture;
using Deckard.Core.Items;
using Deckard.Perception.SkillTrees;
using static Deckard.Perception.Tests.TestScenes;

namespace Deckard.Perception.Tests;

public class SkillTreeLocatorTests
{
    // The first three Paladin cluster hubs (tree coordinates from Season 15 data).
    private static readonly SkillTree Tree = new(
        [
            new SkillNode(1848, "cluster", "", "Basic", 0, 0, -80, -6083, 0),
            new SkillNode(1845, "cluster", "", "Core", 0, 3, -2370, -3884, 0),
            new SkillNode(1842, "cluster", "", "Aura", 0, 4, 2233, -1783, 0),
        ],
        []);

    private static readonly (byte, byte, byte) TabRed = (200, 12, 2);   // lit "Skill Tree" tab
    private static readonly (byte, byte, byte) TrunkRed = (130, 18, 18); // trunk and hub frames, measured
    private static readonly (byte, byte, byte) Emblem = (130, 125, 105); // a hub's grey centre
    private static readonly (byte, byte, byte) LockedEmblem = (90, 70, 10); // a locked hub's gold diamond, measured

    private static double ScaleX(double zoom, int height = 1440) => height * SkillTreeLocator.ScaleXPerHeight * zoom;
    private static double ScaleY(double zoom, int height = 1440) => height * SkillTreeLocator.ScaleYPerHeight * zoom;

    // The same hubs with eight skill nodes around each, for tests that need nodes to fit.
    private static readonly SkillTree TreeWithNodes = new(
        [
            .. Tree.Nodes,
            .. from hub in Tree.Nodes
               from dx in new[] { -2500, -1500, 1500, 2500 }
               from dy in new[] { -500, 500 }
               select new SkillNode(0, "passive", "", "", 3, 0, hub.X + dx, hub.Y + dy, 0),
        ],
        []);

    /// <summary>
    /// A skill tree screen (3440x1440 unless given) with the trunk drawn at the given pan offset and zoom; a locked
    /// trunk is dark instead of red. With <paramref name="nodes"/>, the nodes of <see cref="TreeWithNodes"/> are drawn
    /// as light icons. Sizes are measured at 1440p and scale with the screen height, as the game's UI does.
    /// </summary>
    private static Frame Screen(double offsetX, double offsetY, bool tabLit = true, double zoom = 1, bool locked = false, bool nodes = false,
        int width = 3440, int height = 1440)
    {
        var s = height / 1440.0;
        var trunk = locked ? ((byte)45, (byte)42, (byte)40) : TrunkRed;
        var frame = Scene(width, height);
        Fill(frame, new PixelRect(0, 0, width, height), (20, 20, 16));
        var hubs = Tree.Nodes.Select(n => (n.X * ScaleX(zoom, height) + offsetX, n.Y * ScaleY(zoom, height) + offsetY)).ToList();
        for (var i = 1; i < hubs.Count; i++)
            ThickLine(frame, hubs[i - 1], hubs[i], 12 * zoom * s, trunk);
        foreach (var (x, y) in hubs)
        {
            Disc(frame, x, y, 60 * zoom * s, trunk);
            Disc(frame, x, y, 26 * zoom * s, locked ? LockedEmblem : Emblem);
        }
        if (nodes)
            foreach (var node in TreeWithNodes.Nodes.Where(n => n.Kind == "passive"))
                Disc(frame, node.X * ScaleX(zoom, height) + offsetX, node.Y * ScaleY(zoom, height) + offsetY, 14 * zoom * s, (150, 140, 120));
        // The game's dark tab bar over the tree's top, with the tabs on it.
        Fill(frame, new PixelRect(0, 0, width, (int)(height * 0.05)), (16, 19, 19));
        if (tabLit)
            Fill(frame, new PixelRect((int)(width / 2 - (112 + 52) * s), (int)(36 * s), (int)(104 * s), (int)(18 * s)), TabRed);
        return frame;
    }

    [Fact]
    public void TellsTheDockedPanelFromTheFullScreen()
    {
        // The Abilities panel on the right: its frame dark, its Skill Tree tab lit (x 2800-2930, y 124-170 at 3440x1440);
        // the game world beside it, here a red-lit one.
        using var docked = Scene(3440, 1440);
        Fill(docked, new PixelRect(0, 0, 3440, 1440), (90, 18, 18));
        Fill(docked, new PixelRect(2540, 0, 900, 1440), (20, 25, 25));
        Fill(docked, new PixelRect(2805, 128, 120, 38), TabRed);
        using var redWorld = Scene(3440, 1440);
        Fill(redWorld, new PixelRect(0, 0, 3440, 1440), (90, 18, 18));
        Fill(redWorld, new PixelRect(2805, 128, 120, 38), TabRed);

        Assert.Equal(Deckard.Perception.ScreenLayout.Docked, SkillTreeLocator.Layout(docked));
        Assert.Equal(Deckard.Perception.ScreenLayout.Full, SkillTreeLocator.Layout(Screen(1720, 700)));
        Assert.Null(SkillTreeLocator.Layout(redWorld));
    }

    [Fact]
    public void ARedWorldIsNoSkillTree()
    {
        // The Worldstone Chamber: the whole scene red, the tab's place too. The tree's tab bar would be dark.
        using var frame = Screen(1720, 700);
        Fill(frame, new PixelRect(0, 0, 3440, (int)(1440 * 0.05)), (90, 18, 18));

        Assert.False(SkillTreeLocator.IsOpen(frame));
        Assert.Null(new SkillTreeLocator(Tree).Locate(frame));
    }

    /// <summary>Every hub must land within 8 px of where it was drawn (skill frames are ~70 px across).</summary>
    private static void AssertHubsAt(TreePlacement? placement, double offsetX, double offsetY, double zoom)
    {
        Assert.NotNull(placement);
        foreach (var hub in Tree.Nodes)
        {
            var (x, y) = placement.Value.ToScreen(hub.X, hub.Y);
            Assert.InRange(x, hub.X * ScaleX(zoom) + offsetX - 8, hub.X * ScaleX(zoom) + offsetX + 8);
            Assert.InRange(y, hub.Y * ScaleY(zoom) + offsetY - 8, hub.Y * ScaleY(zoom) + offsetY + 8);
        }
    }

    /// <summary>
    /// Other players' screens: 16:9 at 1080p, 1440p and 4K, and a 16:10 laptop. The tree is placed as on the 3440x1440
    /// test (the same distance from the screen's middle, scaled), and every hub must be found within
    /// <paramref name="tolerance"/> px at 1440p, scaled the same way. The zoom comes from a hub's size measured in
    /// 5 px rings, so it's coarser on smaller screens: at 1920x1200 the far hubs land ~9 px off, still well inside a
    /// ~70 px skill frame.
    /// </summary>
    [Theory]
    [InlineData(1920, 1080, 8)]
    [InlineData(2560, 1440, 8)]
    [InlineData(3840, 2160, 8)]
    [InlineData(1920, 1200, 12)]
    public void FindsTheTreeAtOtherResolutions(int width, int height, double tolerance)
    {
        var s = height / 1440.0;
        var (offsetX, offsetY) = (width / 2 + (1830 - 1720) * s, 1680 * s);
        using var frame = Screen(offsetX, offsetY, width: width, height: height);

        var placement = new SkillTreeLocator(Tree).Locate(frame);

        Assert.NotNull(placement);
        foreach (var hub in Tree.Nodes)
        {
            var (x, y) = placement.Value.ToScreen(hub.X, hub.Y);
            Assert.InRange(x, hub.X * ScaleX(1, height) + offsetX - tolerance * s, hub.X * ScaleX(1, height) + offsetX + tolerance * s);
            Assert.InRange(y, hub.Y * ScaleY(1, height) + offsetY - tolerance * s, hub.Y * ScaleY(1, height) + offsetY + tolerance * s);
        }
    }

    [Fact]
    public void FindsThePanOffset()
    {
        using var frame = Screen(1830, 1680);

        AssertHubsAt(new SkillTreeLocator(Tree).Locate(frame), 1830, 1680, 1);
    }

    [Theory]
    [InlineData(1.17)] // the zoom seen on a live screenshot
    [InlineData(0.95)] // the game's minimum zoom
    public void FindsTheZoom(double zoom)
    {
        // Pan so the hubs stay inside the tree panel at this zoom.
        double offsetX = 1720 + 80 * ScaleX(zoom), offsetY = 400 + 6083 * ScaleY(zoom);
        using var frame = Screen(offsetX, offsetY, zoom: zoom);

        var placement = new SkillTreeLocator(Tree).Locate(frame);

        Assert.InRange(placement!.Value.Zoom, zoom * 0.97, zoom * 1.03);
        AssertHubsAt(placement, offsetX, offsetY, zoom);
    }

    [Fact]
    public void FindsTheZoomFromALoneHub()
    {
        // At the game's maximum zoom (~2.25) only the Core hub is on screen, with its trunk running off both ways.
        // The synthetic hub is a solid red ring from radius 26 to 60, whose red band centres at ~43 px: close to the
        // game's 43.8 px at zoom 1, so the size-based zoom estimate sees a realistic hub.
        const double zoom = 2.25;
        double offsetX = 1720 + 2370 * ScaleX(zoom), offsetY = 700 + 3884 * ScaleY(zoom);
        using var frame = Screen(offsetX, offsetY, zoom: zoom);

        var placement = new SkillTreeLocator(Tree).Locate(frame);

        Assert.NotNull(placement);
        // A lone hub's centre comes from its red centroid, which the trunk leaving one side pulls by a few pixels;
        // at this zoom a skill frame is ~157 px across, so 12 px still lands every frame on its node.
        var (x, y) = placement.Value.ToScreen(-2370, -3884);
        Assert.InRange(x, 1720 - 12, 1720 + 12);
        Assert.InRange(y, 700 - 12, 700 + 12);
        Assert.InRange(placement.Value.Zoom, zoom * 0.9, zoom * 1.1);
    }

    [Fact]
    public void FollowsADrag()
    {
        var locator = new SkillTreeLocator(Tree);
        using var before = Screen(1830, 1680);
        using var after = Screen(1870, 1640); // dragged 40 px right and up

        locator.Locate(before);

        AssertHubsAt(locator.Locate(after), 1870, 1640, 1);
    }

    [Fact]
    public void FollowsAZoomButHoldsAStillTree()
    {
        var locator = new SkillTreeLocator(Tree);
        double offsetX = 1720 + 80 * ScaleX(1.17), offsetY = 400 + 6083 * ScaleY(1.17);
        using var before = Screen(1830, 1680);
        using var after = Screen(offsetX, offsetY, zoom: 1.17);

        locator.Locate(before);
        var zoomed = locator.Locate(after);

        Assert.InRange(zoomed!.Value.Zoom, 1.17 * 0.97, 1.17 * 1.03);
        Assert.Equal(zoomed, locator.Locate(after)); // the same frame again doesn't move the highlight
    }

    [Fact]
    public void FollowsADragIntoTheLockedTree()
    {
        // Locked parts of the tree have no red at all; only the nodes show where it went.
        var locator = new SkillTreeLocator(TreeWithNodes);
        using var unlocked = Screen(1830, 1680, nodes: true);
        using var locked = Screen(1890, 1610, locked: true, nodes: true); // dragged 60 px right and 70 up

        AssertHubsAt(locator.Locate(unlocked), 1830, 1680, 1);

        AssertHubsAt(locator.Locate(locked), 1890, 1610, 1);
    }

    [Fact]
    public void FindsALockedTreeFromItsGoldHubs()
    {
        // The tree reopens where it was left, which on a new character is often all locked: no red, no previous frame.
        using var frame = Screen(1830, 1680, locked: true, nodes: true);

        AssertHubsAt(new SkillTreeLocator(TreeWithNodes).Locate(frame), 1830, 1680, 1);
    }

    [Fact]
    public void ReportsClosedWithoutTheLitTab()
    {
        using var frame = Screen(1830, 1680, tabLit: false);

        Assert.Null(new SkillTreeLocator(Tree).Locate(frame));
    }
}
