using Deckard.Core.Capture;
using Deckard.Core.Items;

namespace Deckard.Perception.SkillTrees;

/// <summary>
/// Whether the skill tree on screen belongs to a build's class. Every class's tree fits the screen about equally well
/// by layout alone, but each class's skill icons are its own art: compare the screen at the build's skill nodes with
/// their icons. How well an icon correlates varies a lot with the view (zoom, the rank badge, dimmed skills), so each
/// skill is judged against the class's other skill icons at the same spot: on the build's class its own icon fits
/// clearly better, on another class's tree no icon does. Measured on four 3440x1440 Paladin captures: the Paladin
/// margin 0.19-0.37, other classes' 0.09 or less. Samples average their cells: icon art is thin bright lines (the
/// Paladin's stained glass), and single pixels a few off the line made right icons score below wrong ones.
/// </summary>
public static class SkillIconCheck
{
    private const int Grid = 8;        // icons are compared as 8x8 grayscale cell averages
    private const double Inset = 0.25; // only the inner art: the edges hold the game's frame and the rank badge
    private const int MinIcons = 6;    // fewer visible skills say too little to judge
    public const double Threshold = 0.13;

    /// <summary>
    /// How much better the screen at the tree's visible skills correlates with each skill's own icon than with the
    /// class's other skill icons, on average; null when too few skills with known icons are on screen. Icons are BGRA
    /// frames with alpha; brightness is ignored, so locked (dimmed) skills still count.
    /// </summary>
    public static double? Similarity(Frame frame, TreePlacement placement, SkillTree tree, IReadOnlyDictionary<long, Frame> icons)
    {
        var panel = SkillTreeLocator.Panel(frame, placement.Layout);
        var size = 0.036 * frame.Height * placement.Zoom; // the icon inside a skill's frame (~0.048 of the height at zoom 1)
        var expected = tree.Nodes.Where(n => n.Kind == "skill" && icons.ContainsKey(n.Icon)).Select(n => n.Icon).Distinct()
            .ToDictionary(id => id, id =>
            {
                var icon = icons[id];
                var values = Sample(icon, icon.Width * Inset, icon.Height * Inset, icon.Width * (1 - 2 * Inset), icon.Height * (1 - 2 * Inset), useAlpha: true, out var mask);
                return (Values: values, Mask: mask);
            });
        var margins = new List<double>();
        var bar = SkillTreeLocator.AssignmentBar(frame.Width, frame.Height, placement.Layout);
        foreach (var node in tree.Nodes)
        {
            if (node.Kind != "skill" || !expected.TryGetValue(node.Icon, out var own))
                continue;
            var (x, y) = placement.ToScreen(node.X, node.Y);
            if (x - size < panel.X || y - size < panel.Y || x + size >= panel.Right || y + size >= panel.Bottom)
                continue;
            if (x + size > bar.X && x - size < bar.Right && y + size > bar.Y && y - size < bar.Bottom)
                continue; // under the Skill Assignment bar or its glowing chevron: not the icon's art
            var seen = Sample(frame, x - size * (0.5 - Inset), y - size * (0.5 - Inset), size * (1 - 2 * Inset), size * (1 - 2 * Inset), useAlpha: false, out _);
            var others = expected.Where(e => e.Key != node.Icon).Select(e => Correlation(e.Value.Values, seen, e.Value.Mask)).OfType<double>().ToList();
            if (Correlation(own.Values, seen, own.Mask) is { } score && others.Count > 0)
                margins.Add(score - others.Average());
        }
        return margins.Count >= MinIcons ? margins.Average() : null;
    }

    /// <summary>
    /// The brightest channel of a region averaged over Grid x Grid cells; with alpha, mostly transparent cells are
    /// masked out.
    /// </summary>
    private static double[] Sample(Frame frame, double x0, double y0, double width, double height, bool useAlpha, out bool[] mask)
    {
        var values = new double[Grid * Grid];
        mask = new bool[Grid * Grid];
        var px = frame.Pixels;
        for (var j = 0; j < Grid; j++)
            for (var i = 0; i < Grid; i++)
            {
                int left = Math.Max(0, (int)(x0 + i * width / Grid)), right = Math.Min(frame.Width, (int)(x0 + (i + 1) * width / Grid));
                int top = Math.Max(0, (int)(y0 + j * height / Grid)), bottom = Math.Min(frame.Height, (int)(y0 + (j + 1) * height / Grid));
                double sum = 0, alpha = 0;
                var count = 0;
                for (var y = top; y < bottom; y++)
                    for (var x = left; x < right; x++)
                    {
                        var o = y * frame.Stride + x * Frame.BytesPerPixel;
                        sum += Math.Max(px[o], Math.Max(px[o + 1], px[o + 2]));
                        alpha += px[o + 3];
                        count++;
                    }
                if (count == 0)
                    continue;
                values[j * Grid + i] = sum / count;
                mask[j * Grid + i] = !useAlpha || alpha / count > 128;
            }
        return values;
    }

    /// <summary>Normalised cross-correlation over the masked samples; null when flat or mostly masked.</summary>
    private static double? Correlation(double[] a, double[] b, bool[] mask)
    {
        double meanA = 0, meanB = 0;
        var n = 0;
        for (var i = 0; i < a.Length; i++)
            if (mask[i])
            {
                meanA += a[i];
                meanB += b[i];
                n++;
            }
        if (n < 20)
            return null;
        meanA /= n;
        meanB /= n;
        double ab = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++)
            if (mask[i])
            {
                ab += (a[i] - meanA) * (b[i] - meanB);
                aa += (a[i] - meanA) * (a[i] - meanA);
                bb += (b[i] - meanB) * (b[i] - meanB);
            }
        return aa * bb <= 0 ? null : ab / Math.Sqrt(aa * bb);
    }
}
