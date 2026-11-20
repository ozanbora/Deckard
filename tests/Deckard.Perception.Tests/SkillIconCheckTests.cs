using Deckard.Core.Capture;
using Deckard.Core.Items;
using Deckard.Perception.SkillTrees;
using static Deckard.Perception.Tests.TestScenes;

namespace Deckard.Perception.Tests;

public class SkillIconCheckTests
{
    // Eight skills on a row, each with its own icon id.
    private static readonly SkillTree Tree = new(
        [.. Enumerable.Range(0, 8).Select(i => new SkillNode(i, "skill", "", "", 5, 0, -3500 + i * 1000, 0, 100 + i))],
        []);

    private static readonly TreePlacement Placement = new(1720, 700, 0.0998, 0.187, 1);

    private const int IconSize = 64;

    /// <summary>A distinct opaque icon per id: diagonal stripes whose angle and spacing depend on the id.</summary>
    private static Frame Icon(long id)
    {
        var icon = Frame.Create(IconSize, IconSize, TimeSpan.Zero);
        var px = icon.Pixels;
        for (var y = 0; y < IconSize; y++)
            for (var x = 0; x < IconSize; x++)
            {
                var v = (byte)((x / 4 * (id % 3 + 1) + y / 4 * (id % 5 + 1)) % 7 < 3 ? 220 : 40);
                var o = y * icon.Stride + x * 4;
                (px[o], px[o + 1], px[o + 2], px[o + 3]) = (v, v, v, 255);
            }
        return icon;
    }

    /// <summary>A skill tree screen with the given icons drawn (dimmed, like locked skills) where the tree's skills are.</summary>
    private static Frame Screen(Func<int, long> iconAt)
    {
        var frame = Scene(3440, 1440);
        Fill(frame, new PixelRect(0, 0, 3440, 1440), (20, 20, 16));
        var size = 0.036 * 1440;
        foreach (var node in Tree.Nodes)
        {
            using var icon = Icon(iconAt(node.Node));
            var (cx, cy) = Placement.ToScreen(node.X, node.Y);
            for (var y = 0; y < (int)size; y++)
                for (var x = 0; x < (int)size; x++)
                {
                    var v = (byte)(icon.Pixels[(int)(y * IconSize / size) * icon.Stride + (int)(x * IconSize / size) * 4] / 2);
                    Fill(frame, new PixelRect((int)(cx - size / 2) + x, (int)(cy - size / 2) + y, 1, 1), (v, v, v));
                }
        }
        return frame;
    }

    private static Dictionary<long, Frame> Icons() => Tree.Nodes.ToDictionary(n => n.Icon, n => Icon(n.Icon));

    [Fact]
    public void MatchesTheBuildsOwnIcons()
    {
        using var frame = Screen(i => 100 + i);

        Assert.True(SkillIconCheck.Similarity(frame, Placement, Tree, Icons()) > 0.5);
    }

    [Fact]
    public void RejectsAnotherClassesIcons()
    {
        using var frame = Screen(i => 200 + i * 7); // other art at the same places

        Assert.True(SkillIconCheck.Similarity(frame, Placement, Tree, Icons()) < SkillIconCheck.Threshold);
    }

    [Fact]
    public void IsUnsureWithFewSkillsInView()
    {
        using var frame = Screen(i => 100 + i);
        var panned = Placement with { OffsetX = Placement.OffsetX + 1500 }; // most skills off screen to the right

        Assert.Null(SkillIconCheck.Similarity(frame, panned, Tree, Icons()));
    }
}
