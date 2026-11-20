using System.Text.RegularExpressions;
using Deckard.Core.Capture;
using Deckard.Core.Items;
using Deckard.Perception.Imaging;
using Deckard.Perception.Ocr;

namespace Deckard.Perception.SkillTrees;

/// <summary>
/// Reads the rank the player has in each skill from the in-game tree: the "4/15" under a skill's frame, at its bottom
/// right. The text is small and OCR reads it right most of the time, not always (measured on two 3440x1440 captures:
/// 14 of 19 skills a frame, the rest blank or garbled), so a reading counts only as "N/M" with M the skill's own
/// maximum, and only once two reads in a row agree. A few skills are read at a time, so tracking a drag stays smooth.
/// Modifiers and passives have no rank text; a taken one counts as rank 1 by its red link.
/// </summary>
public sealed partial class SkillRankReader(TextRecognizer ocr)
{
    private readonly Dictionary<int, int> _known = [];   // node -> confirmed rank, as shown: points and item ranks
    private readonly Dictionary<int, int> _pending = []; // node -> a reading waiting for a second
    private readonly Dictionary<int, int> _max = [];     // node -> its rank limit, for points spent
    private readonly Dictionary<int, int> _bonus = [];   // node -> ranks from items, off its tooltip
    private int _next;
    private int? _hovered; // the skill the cursor rested on at the last read, and whose tooltip was read last
    private int? _tooltipRead;

    /// <summary>The rank the tree shows for a skill node (points spent and item ranks), or null when it hasn't been read yet.</summary>
    public int? Rank(int node) => _known.TryGetValue(node, out var rank) ? rank : null;

    /// <summary>The ranks items give a skill (its tooltip's "Item Contribution"), or null when its tooltip hasn't been read.</summary>
    public int? Bonus(int node) => _bonus.TryGetValue(node, out var bonus) ? bonus : null;

    /// <summary>
    /// Item ranks and the ranks the tree showed, read before (saved by the app), so a restart doesn't need every skill
    /// hovered or scrolled past again: a skill out of view keeps the rank it last showed.
    /// </summary>
    public void Remember(IReadOnlyDictionary<int, int> bonuses, IReadOnlyDictionary<int, int>? ranks = null)
    {
        foreach (var (node, bonus) in bonuses)
            _bonus.TryAdd(node, bonus);
        foreach (var (node, rank) in ranks ?? new Dictionary<int, int>())
            _known.TryAdd(node, rank);
    }

    /// <summary>A node's rank as the tree shows it was confirmed or changed: the node and the rank, for the app to keep.</summary>
    public event Action<int, int>? RankRead;

    /// <summary>What the last skill tooltip read came to, for the status line: the skill and its rank, or why it didn't read.</summary>
    public string? LastTooltip { get; private set; }

    /// <summary>A skill's item ranks were read off its tooltip: the node and the ranks, for the app to keep.</summary>
    public event Action<int, int>? BonusRead;

    /// <summary>
    /// The points spent on a node: its shown rank less its item ranks. Without its tooltip read, a rank over the node's
    /// limit (16/15) is item ranks of unknown size, so null; a rank within it counts as spent.
    /// </summary>
    public int? Spent(int node) => Rank(node) is not int rank ? null
        : Bonus(node) is int bonus ? Math.Max(0, rank - bonus)
        : _max.TryGetValue(node, out var max) && rank > max ? null : rank;

    /// <summary>
    /// The tree closed. Ranks stay as last shown: out of view they're the best there is, and a respec shows as its skills
    /// come into view again (two agreeing reads replace them). Item ranks stay: they go with the gear.
    /// </summary>
    public void Forget()
    {
        _pending.Clear();
        _hovered = _tooltipRead = null;
    }

    /// <summary>
    /// Reads the tooltip of the skill the cursor rests on (<paramref name="cursorX"/>, <paramref name="cursorY"/> in frame
    /// pixels): its "RANK 16/15" and "(Item Contribution: 3)", once the cursor has stayed on it for two reads, once per
    /// rest. True when a rank or item rank changed. The tooltip appears beside the skill, so a band around it is read.
    /// </summary>
    public async Task<bool> ReadTooltipAsync(Frame frame, TreePlacement placement, IReadOnlyList<SkillNode> nodes, double cursorX, double cursorY)
    {
        var under = SkillAt(frame, placement, nodes, cursorX, cursorY);
        var resting = under is not null && under.Node == _hovered;
        _hovered = under?.Node;
        if (under is null || !resting || _tooltipRead == under.Node)
            return false;
        // The whole height, and past the tree's panel: docked, the game opens the tooltip left of the panel.
        var left = Math.Max(0, (int)(cursorX - frame.Height * 0.6));
        var right = Math.Min(frame.Width, (int)(cursorX + frame.Height * 0.6));
        var top = 0;
        using var crop = frame.Crop(new PixelRect(left, top, right - left, frame.Height));
        using var prepared = ImageOps.PrepareForOcr(crop, scale: 1);
        var lines = await ocr.RecognizeAsync(prepared);
        if (Tooltip([.. lines.Select(l => l.Text)], under.Name, under.MaxRanks) is not (int rank, var read, int at))
        {
            LastTooltip = $"{under.Name} unread in {frame.Width}x{frame.Height} at ({cursorX:F0},{cursorY:F0}): " + string.Join(" | ", lines.Take(6).Select(l => l.Text));
            return false;
        }
        // At this size OCR drops the Contribution's coloured number, and garbles the words ("(Item Contnbuuon:"): read
        // that line and the RANK above it again at 2x.
        var bonus = read ?? 0;
        if (read is null && lines[at].Bounds is var line)
        {
            var header = new PixelRect(left + line.X - line.Height, top + line.Y - line.Height * 3, line.Width + line.Height * 4, line.Height * 5);
            using var close = frame.Crop(header.Intersect(new PixelRect(0, 0, frame.Width, frame.Height)));
            using var big = ImageOps.PrepareForOcr(close, scale: 2);
            if (Tooltip([under.Name, .. (await ocr.RecognizeAsync(big)).Select(l => l.Text)], under.Name, under.MaxRanks) is (int r, int b, _))
                (rank, bonus) = (r, b);
            else
            {
                LastTooltip = $"{under.Name} {rank}/{under.MaxRanks}, item ranks unread";
                return false; // a number there, unread: better unknown than 0
            }
        }
        _tooltipRead = under.Node;
        LastTooltip = $"{under.Name} {rank}/{under.MaxRanks}, {bonus} from items";
        var changed = Rank(under.Node) != rank || Bonus(under.Node) != bonus;
        (_known[under.Node], _bonus[under.Node], _max[under.Node]) = (rank, bonus, under.MaxRanks);
        BonusRead?.Invoke(under.Node, bonus);
        RankRead?.Invoke(under.Node, rank);
        return changed;
    }

    /// <summary>The skill whose frame is at (<paramref name="x"/>, <paramref name="y"/>) on the tree as placed, or null.</summary>
    public static SkillNode? SkillAt(Frame frame, TreePlacement placement, IEnumerable<SkillNode> nodes, double x, double y)
    {
        var size = 0.048 * frame.Height * placement.Zoom; // a skill's frame
        return nodes.Where(n => n.Kind == "skill").Select(n => (Node: n, At: placement.ToScreen(n.X, n.Y)))
            .Where(s => Math.Abs(s.At.X - x) < size * 0.6 && Math.Abs(s.At.Y - y) < size * 0.6)
            .Select(s => s.Node).FirstOrDefault();
    }

    /// <summary>
    /// A skill tooltip's rank and item ranks: its name, then "RANK 16/15" (the limit being the skill's) and, when items add
    /// ranks, "(Item Contribution: 3)" under it (0 without that line; null when the line is there but its number didn't
    /// read). <c>Line</c> is the Contribution line's index, else the RANK line's. Null when the lines aren't that skill's tooltip.
    /// </summary>
    internal static (int Rank, int? Bonus, int Line)? Tooltip(IReadOnlyList<string> lines, string name, int maxRanks)
    {
        var at = lines.ToList().FindIndex(l => l.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
            return null;
        for (var i = at + 1; i < Math.Min(lines.Count, at + 5); i++)
            if (TooltipRank().Match(lines[i]) is { Success: true } m && int.Parse(m.Groups[2].Value) == maxRanks)
            {
                var rank = int.Parse(m.Groups[1].Value);
                // Matched by its shape, "(Item ...", since OCR misspells the words; its number is the line's last.
                if (i + 1 < lines.Count && ContributionLine().IsMatch(lines[i + 1]))
                    return (rank, LastNumber().Match(lines[i + 1]) is { Success: true } n ? int.Parse(n.Groups[1].Value) : null, i + 1);
                return (rank, 0, i);
            }
        return null;
    }

    /// <summary>
    /// Reads the rank text of up to <paramref name="count"/> of the skills in view, and whether each modifier and passive
    /// in view is taken; true when a confirmed rank changed.
    /// </summary>
    /// <param name="cursorX">The cursor, in frame pixels: the node under it isn't judged, as hovering lights its link like taking it.</param>
    public async Task<bool> ReadAsync(Frame frame, TreePlacement placement, IReadOnlyList<SkillNode> nodes, int count = 3, double cursorX = -1, double cursorY = -1)
    {
        var panel = SkillTreeLocator.Panel(frame, placement.Layout);
        var changed = false;
        // Modifiers and passives show no rank text, but a taken one has its link lit red where it meets the node (rank 1:
        // all a modifier has; a passive may want more, so it stays to take).
        var half = 0.0125 * frame.Height * placement.Zoom; // a modifier's diamond
        foreach (var node in nodes.Where(n => n.Kind is "modifier" or "passive"))
        {
            var (x, y) = placement.ToScreen(node.X, node.Y);
            if (x - half * 2 < panel.X || y - half * 2 < panel.Y || x + half * 2 > panel.Right || y + half * 2 > panel.Bottom)
                continue;
            if (Math.Abs(x - cursorX) < half * 3 && Math.Abs(y - cursorY) < half * 3)
                continue; // hovered: its link lights up as if taken (live, a modifier's mark went on hover)
            changed |= Confirm(node.Node, TakenAt(frame, x, y, half) ? 1 : 0);
        }

        var skills = nodes.Where(n => n.Kind == "skill");
        var size = 0.048 * frame.Height * placement.Zoom; // a skill's frame
        var inView = new List<(SkillNode Node, PixelRect Text)>();
        foreach (var node in skills)
        {
            var (x, y) = placement.ToScreen(node.X, node.Y);
            // The text sits at the frame's bottom right, over its lower edge; OCR wants some margin around it.
            var region = new PixelRect((int)(x - size * 0.6), (int)(y + size * 0.35), (int)(size * 1.5), (int)(size * 0.75));
            if (region.X >= panel.X && region.Y >= panel.Y && region.Right <= panel.Right && region.Bottom <= panel.Bottom)
                inView.Add((node, region));
        }
        for (var i = 0; i < Math.Min(count, inView.Count); i++)
        {
            var (node, region) = inView[(_next + i) % inView.Count];
            using var crop = frame.Crop(region);
            // 2x read most skills best; a narrow "1/15" it misses, 4x with a lower white point often reads.
            int? rank = null;
            foreach (var (scale, white) in new[] { (2, (byte)200), (4, (byte)160) })
            {
                using var prepared = ImageOps.PrepareForOcr(crop, scale: scale, white: white);
                var lines = await ocr.RecognizeAsync(prepared);
                if ((rank = lines.Select(l => Parse(l.Text, node.MaxRanks)).FirstOrDefault(r => r is not null)) is not null)
                    break;
            }
            if (rank is { } read)
            {
                _max[node.Node] = node.MaxRanks;
                changed |= Confirm(node.Node, read);
            }
        }
        _next = inView.Count == 0 ? 0 : (_next + count) % inView.Count;
        return changed;
    }

    /// <summary>Keeps a reading once two reads in a row agree; true when that changed the confirmed rank.</summary>
    private bool Confirm(int node, int read)
    {
        var changed = false;
        if (_pending.TryGetValue(node, out var before) && before == read)
        {
            changed = !_known.TryGetValue(node, out var known) || known != read;
            _known[node] = read;
            if (changed)
                RankRead?.Invoke(node, read);
        }
        _pending[node] = read;
        return changed;
    }

    /// <summary>
    /// A taken modifier or passive: its link lit red where it meets the node. Measured at 3440x1440: 4% red pixels on a
    /// ring 1.1-1.8 diamond halves out on taken ones, at most 1% on the others.
    /// </summary>
    internal static bool TakenAt(Frame frame, double cx, double cy, double half)
    {
        int n = 0, red = 0;
        var px = frame.Pixels;
        for (var a = 0; a < 72; a++)
        {
            double cos = Math.Cos(a * Math.PI / 36), sin = Math.Sin(a * Math.PI / 36);
            for (var r = half * 1.1; r <= half * 1.8; r += 1.5)
            {
                int x = (int)(cx + r * cos), y = (int)(cy + r * sin);
                if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height)
                    continue;
                var o = y * frame.Stride + x * Frame.BytesPerPixel;
                int b = px[o], g = px[o + 1], rr = px[o + 2];
                n++;
                if (rr > 60 && rr > 2 * g && rr > 2 * b)
                    red++;
            }
        }
        return n > 0 && red >= n * 0.025;
    }

    /// <summary>
    /// The rank in a skill's "N/M" text when M is its maximum; null for anything else OCR made of it. N can pass M: items
    /// add ranks over the limit points stop at (16/15).
    /// </summary>
    internal static int? Parse(string text, int maxRanks)
    {
        var m = RankText().Match(text);
        return m.Success && int.Parse(m.Groups[2].Value) == maxRanks ? int.Parse(m.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"^\s*(\d{1,2})\s*/\s*(\d{1,2})\s*$")]
    private static partial Regex RankText();

    [GeneratedRegex(@"RANK\s*(\d{1,2})\s*/\s*(\d{1,2})", RegexOptions.IgnoreCase)]
    private static partial Regex TooltipRank();

    [GeneratedRegex(@"^\W*[Il1|]tem\b", RegexOptions.IgnoreCase)]
    private static partial Regex ContributionLine();

    [GeneratedRegex(@"(\d{1,2})\D*$")]
    private static partial Regex LastNumber();
}
