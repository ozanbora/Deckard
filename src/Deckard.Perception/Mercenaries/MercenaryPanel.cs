using Deckard.Core.Builds;
using Deckard.Core.Capture;
using Deckard.Core.Items;
using Deckard.Perception.Imaging;
using Deckard.Perception.Ocr;

namespace Deckard.Perception.Mercenaries;

/// <summary>The mercenaries the game's Mercenaries tab shows, by class key: the one hired and the reinforcement (null: none or unread).</summary>
/// <param name="Tree">The mercenary whose skill tree is open (its Skills tab) rather than the list of the two; null on the list.</param>
/// <param name="SkillRow">That panel shows its Reinforcement tab: a row of skills to select one from, not the tree.</param>
public sealed record MercenarySighting(string? Hired, string? Reinforcement, string? Tree = null, bool SkillRow = false);

/// <summary>
/// The Mercenaries tab of the panel on the right (measured at 3440x1440): "HIRED MERCENARY", "Active: Subo" in the box at
/// y 335-655 and the reinforcement's box below it ("NO REINFORCEMENT ENLISTED" at y 880), read with OCR and set against
/// the guide's mercenaries.
/// </summary>
public static class MercenaryPanel
{
    /// <summary>The panel's two boxes, with their text.</summary>
    /// <remarks>From the panel's title down: a mercenary's own panel names it there ("VARYANA", y 172 at 1440).</remarks>
    public static PixelRect Area(int width, int height) => new(width - (int)(height * 0.625), (int)(height * 0.105), (int)(height * 0.625), (int)(height * 0.645));

    /// <summary>Where the advice goes: under the hired box, then under the reinforcement's (frame pixels, centred).</summary>
    public static (double X, double HireY, double ReinforcementY) AdviceAt(int width, int height) =>
        (width - height * 0.3125, height * 0.475, height * 0.765); // the reinforcement's: under its "Assists you on command" text

    /// <summary>What the Mercenaries tab shows, or null when it isn't open.</summary>
    public static async Task<MercenarySighting?> ReadAsync(Frame frame, TextRecognizer ocr, AffixCatalog catalog)
    {
        using var crop = frame.Crop(Area(frame.Width, frame.Height));
        using var prepared = ImageOps.PrepareForOcr(crop, scale: 1);
        return Parse([.. (await ocr.RecognizeAsync(prepared)).Select(l => l.Text)], catalog);
    }

    /// <summary>
    /// The tab's lines: the hired mercenary named on its "Active:" line, the reinforcement any other mercenary named
    /// unless "NO REINFORCEMENT ENLISTED"; null when neither line is there (another tab).
    /// </summary>
    public static MercenarySighting? Parse(IReadOnlyList<string> lines, AffixCatalog catalog)
    {
        var active = lines.FirstOrDefault(l => l.Contains("Active", StringComparison.OrdinalIgnoreCase));
        var noReinforcement = lines.Any(l => l.Contains("NO REINFORCEMENT", StringComparison.OrdinalIgnoreCase));
        // Spaces aside: OCR reads the panel's letter-spaced title as "VAR YANA".
        string? Named(string line) =>
            catalog.Mercenaries.FirstOrDefault(m => line.Replace(" ", "").Contains(m.Value.Name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)).Key;
        // A mercenary's own panel on its Skills tab: its name in the title, "Available Points" over its tree.
        // A reinforcement's panel has no tree: "SELECT A SKILL" over a row of its skills.
        var row = lines.Any(l => l.Contains("SELECT A SKILL", StringComparison.OrdinalIgnoreCase));
        if (active is null && (row || lines.Any(l => l.Contains("Available Points", StringComparison.OrdinalIgnoreCase))))
            return lines.Select(Named).FirstOrDefault(k => k is not null) is { } open ? new MercenarySighting(null, null, open, row) : null;
        if (active is null && !noReinforcement)
            return null;
        var hired = active is null ? null : Named(active);
        var reinforcement = noReinforcement ? null
            : lines.Where(l => l != active).Select(Named).FirstOrDefault(k => k is not null && k != hired);
        return new MercenarySighting(hired, reinforcement);
    }

    /// <summary>
    /// What to do about each against the guide: hire its mercenary (instead of the one hired) and pick its skills, enlist
    /// its reinforcement with the skill and the player's skill that calls it. Null for a part the guide doesn't set.
    /// </summary>
    public static (string? Hire, string? Reinforcement) Advise(MercenarySighting seen, PlannedMercenaries plan, AffixCatalog catalog)
    {
        string Name(string key) => catalog.Mercenaries.GetValueOrDefault(key)?.Name ?? key;
        string? hire = null;
        if (plan.Hired is { } hired && catalog.Mercenaries.GetValueOrDefault(hired) is { } merc)
        {
            var picks = merc.InTreeOrder(plan.Picks).Select(n => merc.Nodes.GetValueOrDefault(n)).OfType<string>()
                .Select(k => merc.Skills.GetValueOrDefault(k)?.Name).OfType<string>().ToList();
            hire = seen.Hired == hired
                ? $"{merc.Name} is the guide's mercenary" + (picks.Count switch { 0 => ".", 1 => $": pick {picks[0]}.", _ => $": pick {picks[0]} first, then {string.Join(", ", picks.Skip(1))}." })
                : $"Hire {merc.Name} in The Den" + (seen.Hired is { } other ? $" instead of {Name(other)}" : "") + ": the guide's mercenary.";
        }
        string? reinforcement = null;
        if (plan.Reinforcement is { } support && catalog.Mercenaries.GetValueOrDefault(support) is { } called)
        {
            var skill = plan.ReinforcementSkill is { } k ? called.Skills.GetValueOrDefault(k)?.Name : null;
            var trigger = plan.Trigger is { } t ? catalog.Skills.GetValueOrDefault(t) : null; // a placeholder key names nothing
            // Its skills unlock with Rapport (two at first); the data doesn't say at which rank, so no rank is named.
            var how = (skill is null ? "" : $": {skill} (unlocks with Rapport if not yet)") + (trigger is null ? "" : $", called by {trigger}") + ".";
            reinforcement = seen.Reinforcement == support ? $"{called.Name} is the guide's reinforcement{how}"
                : $"Enlist {called.Name} as reinforcement in The Den" + (seen.Reinforcement is { } other ? $" instead of {Name(other)}" : "") + how;
        }
        return (hire, reinforcement);
    }

    /// <summary>
    /// Where the guide's picks are on a mercenary's open skill tree, in frame pixels, numbered in the order the tree opens
    /// them: the hired mercenary's picks, or the reinforcement's one skill. Empty for a mercenary the guide doesn't use.
    /// The panel doesn't pan or zoom: measured on Varyana's at 3440x1440, the tree's root sits 464 px left of the frame's
    /// right edge and 464 px down, a tree unit being 0.1264 px across and 0.2298 px down.
    /// </summary>
    /// <param name="row">The panel shows the reinforcement's row of skills (<see cref="MercenarySighting.SkillRow"/>): the guide's skill
    /// is marked there. The row lists the mercenary's active skills in the data's order, 120 px apart from 784 px left of
    /// the frame's right edge, at y 1010 (Raheir's at 3440x1440: Bastion is the fifth).</param>
    public static IReadOnlyList<(double X, double Y, int Order, string Name)> TreePicks(string open, PlannedMercenaries plan, AffixCatalog catalog, int width, int height,
        bool row = false)
    {
        if (catalog.Mercenaries.GetValueOrDefault(open) is not { Tree: { } tree } merc || tree.Nodes.FirstOrDefault(n => n.Kind == "cluster") is not { } root)
            return [];
        if (row)
        {
            var active = merc.Skills.Keys.Where(k => tree.Nodes.Any(n => n.Kind == "skill" && n.Skill == k)).ToList();
            return open == plan.Reinforcement && plan.ReinforcementSkill is { } skill && active.IndexOf(skill) is var at and >= 0
                ? [(width - height * 0.5444 + at * height * 0.08333, height * 0.7014, 1, merc.Skills[skill].Name)]
                : [];
        }
        var picks = open == plan.Hired ? merc.InTreeOrder(plan.Picks)
            : open == plan.Reinforcement ? [.. merc.Nodes.Where(n => n.Value == plan.ReinforcementSkill).Select(n => n.Key)]
            : [];
        return [.. picks.Select(id => tree.Nodes.FirstOrDefault(n => n.Node == id)).OfType<SkillNode>().Select((n, i) =>
            (width - height * 0.3222 + (n.X - root.X) * height * 8.778e-5, height * 0.3222 + (n.Y - root.Y) * height * 1.596e-4, i + 1, n.Name))];
    }
}