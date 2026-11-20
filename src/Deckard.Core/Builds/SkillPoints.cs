using Deckard.Core.Items;

namespace Deckard.Core.Builds;

/// <summary>
/// Editing a build's skill points by the tree's own shape: a point goes on a skill, modifier or passive that the tree
/// reaches from a cluster hub through junctions and nodes already taken, up to its rank limit; a node's last point
/// comes off only when nothing taken hangs from it. Unlock levels aren't checked.
/// </summary>
public static class SkillPoints
{
    public static bool CanAdd(SkillTree tree, IReadOnlyDictionary<int, int> ranks, int node)
    {
        var nodes = tree.Nodes.ToDictionary(n => n.Node);
        return nodes.TryGetValue(node, out var n) && Takes(n) && ranks.GetValueOrDefault(node) < n.MaxRanks
            && (ranks.GetValueOrDefault(node) > 0 || Reached(tree, nodes, ranks, node));
    }

    public static bool CanRemove(SkillTree tree, IReadOnlyDictionary<int, int> ranks, int node)
    {
        var rank = ranks.GetValueOrDefault(node);
        if (rank <= 0)
            return false;
        if (rank > 1)
            return true;
        var without = ranks.Where(r => r.Key != node && r.Value > 0).ToDictionary(r => r.Key, r => r.Value);
        var nodes = tree.Nodes.ToDictionary(n => n.Node);
        var reached = Reachable(tree, nodes, without);
        return without.Keys.All(reached.Contains);
    }

    /// <summary>The ranks with one point more (or less) on <paramref name="node"/>, or the same ranks when the rules don't allow it.</summary>
    public static Dictionary<int, int> Change(SkillTree tree, IReadOnlyDictionary<int, int> ranks, int node, bool add)
    {
        var result = ranks.Where(r => r.Value > 0).ToDictionary(r => r.Key, r => r.Value);
        if (add && CanAdd(tree, ranks, node))
            result[node] = result.GetValueOrDefault(node) + 1;
        else if (!add && CanRemove(tree, ranks, node) && --result[node] == 0)
            result.Remove(node);
        return result;
    }

    /// <summary>
    /// What to mark on the in-game tree for leveling step <paramref name="step"/> (target ranks by node): the nodes it adds
    /// over the last earlier step the points spent cover (the step before it when that count is unknown), and earlier nodes
    /// the tree shows short of their rank. Ranks the tree shows (<paramref name="shown"/>, null when unread) include those
    /// from gear, charms and runes, which the "points spent" count leaves out: a node showing its target is dropped, unless
    /// that would leave nothing while the step still has points to take; then the shown ranks are bonus ranks, and the
    /// step's own nodes stay marked.
    /// </summary>
    /// <param name="exact">Whether a node's points are known (its tooltip gave its item ranks), so its shown rank is no
    /// guess: such a node at its target stays dropped. Null: none is.</param>
    public static Dictionary<int, int> ToSpend(IReadOnlyList<SkillStep> steps, int step, int? spent, Func<int, int?> shown, Func<int, bool>? exact = null)
    {
        var target = steps[step].RanksByNode;
        var covered = (spent is int s
            ? steps.Take(step).LastOrDefault(x => x.RanksByNode.Values.Sum() <= s)
            : step > 0 ? steps[step - 1] : null)?.RanksByNode ?? new Dictionary<int, int>();
        var added = target.Where(t => t.Value > covered.GetValueOrDefault(t.Key)).ToDictionary(t => t.Key, t => t.Value);
        var open = added.Where(a => !(shown(a.Key) is int r && r >= a.Value)).ToDictionary(a => a.Key, a => a.Value);
        if (open.Count == 0 && spent is int has && has < target.Values.Sum())
            // Every node "done" with points still to spend on the step: the tree's ranks hold bonus ranks. Not a node whose
            // points its tooltip gave (Apocalypse 18/15 with 3 from items is 15 spent: done, whatever the total says).
            open = added.Where(a => exact?.Invoke(a.Key) != true).ToDictionary(a => a.Key, a => a.Value);
        foreach (var (node, rank) in target)
            if (!added.ContainsKey(node) && shown(node) is int r && r < rank)
                open[node] = rank; // an earlier node the tree shows short
        return open;
    }

    /// <summary>
    /// Where the player's next <paramref name="available"/> points go, in the guide's order: nodes of earlier steps the tree
    /// shows short first (they come before the step), then the step's own in the order the guide lists them; each with its
    /// target rank and the points it takes now. The same for the same reads, wherever the view is.
    /// </summary>
    public static List<(int Node, int Target, int Points)> NextPoints(IReadOnlyList<SkillStep> steps, int step, int? spent, int available,
        Func<int, int?> shown, Func<int, bool>? exact = null)
    {
        var open = ToSpend(steps, step, spent, shown, exact);
        var covered = (spent is int s ? steps.Take(step).LastOrDefault(x => x.RanksByNode.Values.Sum() <= s) : step > 0 ? steps[step - 1] : null)?.RanksByNode
            ?? new Dictionary<int, int>();
        var target = steps[step].RanksByNode;
        // Earlier nodes first, by the step that first wants them at their rank; then the step's own, as the guide lists them.
        int FirstStep(int node) => Enumerable.Range(0, step + 1).First(i => steps[i].RanksByNode.GetValueOrDefault(node) >= target[node]);
        var ordered = target.Keys.Where(open.ContainsKey)
            .OrderBy(n => covered.GetValueOrDefault(n) >= target[n] ? 0 : 1).ThenBy(FirstStep).ToList();
        var result = new List<(int Node, int Target, int Points)>();
        var left = available;
        foreach (var node in ordered)
        {
            if (left <= 0)
                break;
            // What it has: as the tree shows it, else what the points spent cover; at least one point to take.
            var has = shown(node) ?? covered.GetValueOrDefault(node);
            var points = Math.Min(left, Math.Max(1, target[node] - has));
            result.Add((node, target[node], points));
            left -= points;
        }
        return result;
    }

    private static bool Takes(SkillNode n) => n.Kind is "skill" or "modifier" or "passive" && n.MaxRanks > 0;

    /// <summary>Whether a free node touches the taken tree: a neighbour, through junctions, is a hub or a taken node.</summary>
    private static bool Reached(SkillTree tree, Dictionary<int, SkillNode> nodes, IReadOnlyDictionary<int, int> ranks, int node) =>
        Neighbours(tree, node, nodes).Any(other => nodes[other].Kind == "cluster" || ranks.GetValueOrDefault(other) > 0);

    /// <summary>Every taken node joined to a hub through junctions and other taken nodes.</summary>
    private static HashSet<int> Reachable(SkillTree tree, Dictionary<int, SkillNode> nodes, IReadOnlyDictionary<int, int> ranks)
    {
        var reached = new HashSet<int>();
        var queue = new Queue<int>(nodes.Values.Where(n => n.Kind == "cluster").Select(n => n.Node));
        while (queue.TryDequeue(out var at))
            foreach (var next in Neighbours(tree, at, nodes))
                if (ranks.GetValueOrDefault(next) > 0 && reached.Add(next))
                    queue.Enqueue(next);
        return reached;
    }

    /// <summary>The hubs and point-taking nodes next to <paramref name="node"/>, looking through junctions.</summary>
    private static HashSet<int> Neighbours(SkillTree tree, int node, Dictionary<int, SkillNode> nodes)
    {
        var found = new HashSet<int>();
        var seen = new HashSet<int> { node };
        var queue = new Queue<int>([node]);
        while (queue.TryDequeue(out var at))
            foreach (var link in tree.Links)
            {
                var other = link.A == at ? link.B : link.B == at ? link.A : -1;
                if (other < 0 || !nodes.TryGetValue(other, out var n) || !seen.Add(other))
                    continue;
                if (n.Kind == "junction")
                    queue.Enqueue(other);
                else
                    found.Add(other);
            }
        return found;
    }
}
