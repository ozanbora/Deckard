using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deckard.Core.Items;

namespace Deckard.Core.Builds;

/// <summary>An affix the build wants, by game affix id (the ids in affix-templates.json).</summary>
/// <param name="Values">The planner's rolled values, raw as the game stores them (0.08 for 8%); see <c>AffixText.Fill</c>.</param>
public sealed record PlannedAffix(long Id, bool Greater, IReadOnlyList<double>? Values = null);

/// <param name="Slot">Equipment slot as numbered by the game: 4 helm, 5 chest, 6 off-hand, 7 main hand, 13 gloves,
/// 14 pants, 15 boots, 16/17 rings, 18 amulet, 20 seal, 21-26 charms.</param>
/// <param name="ItemKey">Game item key, e.g. "Helm_Unique_Generic_005" (Leoric's Crown) or a legendary base.</param>
/// <param name="Power">Item power, 0 if the planner doesn't say.</param>
/// <param name="Sockets">Keys of the gems or runes in its sockets.</param>
public sealed record PlannedItem(
    int Slot, string ItemKey, IReadOnlyList<PlannedAffix> Affixes, IReadOnlyList<PlannedAffix> Tempered, IReadOnlyList<PlannedAffix> Aspects,
    int Power = 0, IReadOnlyList<string>? Sockets = null);

/// <summary>Skill points after one planner step (leveling guides have several, endgame guides usually one).</summary>
public sealed record SkillStep(string Name, IReadOnlyDictionary<int, int> RanksByNode);

/// <summary>
/// One paragon board as placed in a build. <paramref name="Nodes"/> are the allocated cells as indexes into the
/// board's unrotated grid (row * width + column); the board is turned <paramref name="Rotation"/> quarter turns
/// clockwise and sits at board-grid position (<paramref name="X"/>, <paramref name="Y"/>), y growing downward, so
/// neighbouring boards' gates touch.
/// </summary>
public sealed record PlacedBoard(string Id, int Rotation, int X, int Y, string? Glyph, int GlyphLevel, IReadOnlySet<int> Nodes)
{
    /// <summary>Where cell <paramref name="index"/> of this board lands on the whole paragon, in cells (y downward).</summary>
    public (int X, int Y) Cell(int index, int width)
    {
        int row = index / width, column = index % width;
        for (var turn = 0; turn < (Rotation % 4 + 4) % 4; turn++)
            (row, column) = (column, width - 1 - row); // a quarter turn clockwise: the top edge becomes the right edge
        return (X * width + column, Y * width + row);
    }
}

/// <summary>Paragon boards after one planner step (guides step through glyph levels, e.g. "Level 50 Glyphs").</summary>
public sealed record ParagonStep(string Name, IReadOnlyList<PlacedBoard> Boards);

/// <param name="ClassIndex">Index into the catalog's class list (0 Sorcerer … 6 Paladin, 7 Warlock).</param>
/// <param name="SkillBar">The six equipped skills, by skill key, in bar order.</param>
/// <param name="SkillNotes">The guide's notes on how to use the skills, as plain text.</param>
public sealed record BuildVariant(
    string Name, int ClassIndex, int Level, IReadOnlyDictionary<int, PlannedItem> Items,
    IReadOnlyList<string> SkillBar, IReadOnlyList<SkillStep> SkillSteps, string SkillNotes, IReadOnlyList<ParagonStep> ParagonSteps)
{
    /// <summary>Class mechanics the build picks, by skill key: the Warlock's soul shard, then its fragment. Empty when the source has none.</summary>
    public IReadOnlyList<string> ClassMechanics { get; init; } = [];

    /// <summary>The mercenaries the guide hires and calls as reinforcement; null when the source has none.</summary>
    public PlannedMercenaries? Mercenaries { get; init; }
}

/// <summary>A guide's mercenaries, by the game's keys (names from <c>AffixCatalog.Mercenaries</c>).</summary>
/// <param name="Hired">The mercenary to hire ("MercenaryClass_BerserkerCrone"), or null.</param>
/// <param name="Picks">The hired mercenary's skill tree nodes the guide picks, by node id.</param>
/// <param name="Reinforcement">The mercenary called as reinforcement, or null.</param>
/// <param name="ReinforcementSkill">The reinforcement's skill, by key, or null.</param>
/// <param name="Trigger">The player's skill that calls the reinforcement, by key, or null.</param>
/// <param name="Notes">The guide's notes on its mercenaries, as plain text.</param>
public sealed record PlannedMercenaries(string? Hired, IReadOnlyList<int> Picks, string? Reinforcement, string? ReinforcementSkill, string? Trigger, string Notes);

/// <summary>A planner build: one or more variants (e.g. Starter, Midgame, Endgame, Bossing).</summary>
public sealed record BuildPlan(string Id, string Name, int Season, IReadOnlyList<BuildVariant> Variants, int DefaultVariant);

/// <summary>
/// Imports builds from a link: d4builds.gg (<see cref="D4BuildsImport"/>), Icy Veins' planner
/// (<see cref="IcyVeinsImport"/>) or maxroll.gg: a build guide link (its embedded planner is used), a planner link
/// (maxroll.gg/d4/planner/&lt;id&gt;#&lt;variant&gt;) or a bare planner id. Maxroll profiles come from maxroll's planner
/// backend, the same request the planner page makes.
/// </summary>
public static partial class BuildPlanLoader
{
    private const string ProfileUrl = "https://planners.maxroll.gg/profiles/d4/";

    /// <param name="catalog">The season's data: d4builds and Icy Veins builds are matched against it by name.</param>
    public static async Task<BuildPlan> LoadAsync(string link, HttpClient http, AffixCatalog catalog, CancellationToken ct = default)
    {
        link = link.Trim();
        if (D4BuildsImport.ParseLink(link) is { } d4)
            return await D4BuildsImport.LoadAsync(d4.Path, d4.Variant, catalog, http, ct);
        if (IcyVeinsImport.ParseLink(link) is { } uid)
            return await IcyVeinsImport.LoadAsync(uid, catalog, http, ct);
        if (IcyVeinsImport.IsGuideLink(link))
            throw new FormatException("Icy Veins guide pages can't be read by apps. Open the guide's planner and paste that link (icy-veins.com/d4/planner/?uid=…).");
        var planner = ParsePlannerLink(link);
        if (planner is null && Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            planner = ParsePlannerLink(await http.GetStringAsync(uri, ct)); // a guide page embeds its planner link
        if (planner is null && BareId().IsMatch(link))
            planner = (link, null);
        var (id, variant) = planner ?? throw new FormatException("No build found. Paste a maxroll, d4builds or Icy Veins planner link.");
        return Parse(await http.GetStringAsync(ProfileUrl + id, ct), variant, id);
    }

    /// <summary>Finds "maxroll.gg/d4/planner/&lt;id&gt;#&lt;n&gt;" in a link or page; n is 1-based in the link.</summary>
    public static (string Id, int? Variant)? ParsePlannerLink(string text)
    {
        var m = PlannerLink().Match(text);
        if (!m.Success)
            return null;
        return (m.Groups[1].Value, m.Groups[2].Success ? int.Parse(m.Groups[2].Value) - 1 : null);
    }

    /// <summary>Parses a planner profile response. <paramref name="variant"/> (0-based) overrides the profile's active one.</summary>
    public static BuildPlan Parse(string profileJson, int? variant = null, string id = "")
    {
        using var profile = JsonDocument.Parse(profileJson);
        var root = profile.RootElement;
        // The build itself is a JSON document stored as a string.
        using var data = JsonDocument.Parse(root.GetProperty("data").GetString() ?? "{}");
        var d = data.RootElement;
        var items = d.GetProperty("items");

        var variants = new List<BuildVariant>();
        foreach (var v in d.GetProperty("profiles").EnumerateArray())
        {
            var planned = new Dictionary<int, PlannedItem>();
            foreach (var slot in v.GetProperty("items").EnumerateObject())
            {
                if (!items.TryGetProperty(slot.Value.ToString(), out var item))
                    continue;
                planned[int.Parse(slot.Name)] = new PlannedItem(int.Parse(slot.Name), item.GetProperty("id").GetString() ?? "",
                    Affixes(item, "explicits"), Affixes(item, "tempered"), Affixes(item, "aspects"),
                    item.TryGetProperty("power", out var power) ? power.GetInt32() : 0,
                    item.TryGetProperty("sockets", out var sockets) ? [.. sockets.EnumerateArray().Select(s => s.GetString() ?? "")] : []);
            }

            var steps = v.TryGetProperty("skillTree", out var tree) && tree.TryGetProperty("steps", out var s)
                ? s.EnumerateArray().Select(step => new SkillStep(
                    step.GetProperty("name").GetString() ?? "",
                    step.GetProperty("data").EnumerateObject().Where(n => n.Value.GetInt32() > 0).ToDictionary(n => int.Parse(n.Name), n => n.Value.GetInt32()))).ToList()
                : [];
            var notes = v.TryGetProperty("widgetNotes", out var wn) && wn.TryGetProperty("skills", out var sn) ? NoteText(sn) : "";
            var paragon = v.TryGetProperty("paragon", out var pg) && pg.TryGetProperty("steps", out var ps)
                ? ps.EnumerateArray().Select(step => new ParagonStep(
                    step.GetProperty("name").GetString() ?? "",
                    [.. step.GetProperty("data").EnumerateArray().Select(Board)])).ToList()
                : [];

            variants.Add(new BuildVariant(
                v.GetProperty("name").GetString() ?? $"Variant {variants.Count + 1}",
                v.GetProperty("class").GetInt32(),
                v.TryGetProperty("level", out var level) ? level.GetInt32() : 0,
                planned,
                v.TryGetProperty("skillBar", out var bar) ? [.. bar.EnumerateArray().Select(x => x.GetString() ?? "")] : [],
                steps,
                notes,
                paragon)
            {
                ClassMechanics = [.. new[] { "warlockShard", "warlockFragment" }
                    .Select(p => v.TryGetProperty(p, out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null)
                    .OfType<string>().Where(m => m.Length > 0)],
                Mercenaries = Mercenaries(v),
            });
        }

        var active = variant ?? (d.TryGetProperty("activeProfile", out var a) ? a.GetInt32() : 0);
        return new BuildPlan(
            id.Length > 0 ? id : root.GetProperty("id").GetString() ?? "",
            root.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            d.TryGetProperty("season", out var season) ? season.GetInt32() : 0,
            variants,
            Math.Clamp(active, 0, Math.Max(0, variants.Count - 1)));
    }

    private static PlacedBoard Board(JsonElement b)
    {
        var position = b.TryGetProperty("position", out var p) ? p : default;
        int Coordinate(string axis) => position.ValueKind == JsonValueKind.Object && position.TryGetProperty(axis, out var c) ? c.GetInt32() : 0;
        // Allocated nodes are an object of "index": 1; an empty board is stored as an empty object or array.
        var nodes = b.TryGetProperty("nodes", out var n) && n.ValueKind == JsonValueKind.Object
            ? n.EnumerateObject().Where(x => x.Value.ValueKind != JsonValueKind.Number || x.Value.GetInt32() > 0).Select(x => int.Parse(x.Name)).ToHashSet()
            : [];
        return new PlacedBoard(
            b.GetProperty("id").GetString() ?? "",
            b.TryGetProperty("rotation", out var r) ? r.GetInt32() : 0,
            Coordinate("x"), Coordinate("y"),
            b.TryGetProperty("glyph", out var g) ? g.GetString() : null,
            b.TryGetProperty("glyphLevel", out var gl) ? gl.GetInt32() : 0,
            nodes);
    }

    private static List<PlannedAffix> Affixes(JsonElement item, string property) =>
        item.TryGetProperty(property, out var list)
            ? [.. list.EnumerateArray().Select(x => new PlannedAffix(
                x.GetProperty("nid").GetInt64(),
                x.TryGetProperty("greater", out var g) && g.GetBoolean(),
                x.TryGetProperty("values", out var v) ? [.. v.EnumerateArray().Select(n => n.GetDouble())] : []))]
            : [];

    /// <summary>
    /// A planner profile's mercenaries: <c>mercenary</c> (<c>id</c> hired, <c>tree</c> node id → 1 when picked,
    /// <c>support</c> the reinforcement, <c>supportSkills</c> its skill and the player's skill that calls it) and the
    /// mercenary notes. Null when the profile has neither.
    /// </summary>
    private static PlannedMercenaries? Mercenaries(JsonElement profile)
    {
        static string? Key(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
        var notes = profile.TryGetProperty("widgetNotes", out var wn) && wn.TryGetProperty("mercenary", out var mn) ? NoteText(mn) : "";
        if (!profile.TryGetProperty("mercenary", out var m) || m.ValueKind != JsonValueKind.Object)
            return notes.Length > 0 ? new PlannedMercenaries(null, [], null, null, null, notes) : null;
        var picks = m.TryGetProperty("tree", out var tree) && tree.ValueKind == JsonValueKind.Object
            ? tree.EnumerateObject().Where(n => n.Value.ValueKind == JsonValueKind.Number && n.Value.GetInt32() > 0 && int.TryParse(n.Name, out _))
                .Select(n => int.Parse(n.Name, System.Globalization.CultureInfo.InvariantCulture)).ToList()
            : [];
        var support = m.TryGetProperty("supportSkills", out var ss) && ss.ValueKind == JsonValueKind.Array
            ? [.. ss.EnumerateArray().Select(s => s.ValueKind == JsonValueKind.String ? s.GetString() : null)] : new List<string?>();
        var plan = new PlannedMercenaries(Key(m, "id"), picks, Key(m, "support"), support.ElementAtOrDefault(0), support.ElementAtOrDefault(1), notes);
        return plan.Hired is null && plan.Reinforcement is null && notes.Length == 0 ? null : plan;
    }

    /// <summary>Planner notes are rich-text trees; keep their text, one paragraph per line.</summary>
    private static string NoteText(JsonElement note)
    {
        var sb = new StringBuilder();
        void Walk(JsonElement node)
        {
            if (node.TryGetProperty("text", out var text))
                sb.Append(text.GetString());
            if (node.TryGetProperty("children", out var children))
                foreach (var child in children.EnumerateArray())
                    Walk(child);
            if (node.TryGetProperty("type", out var type) && type.GetString() is "paragraph" or "listitem" or "heading")
                sb.Append('\n');
        }
        if (note.TryGetProperty("root", out var root))
            Walk(root);
        return Regex.Replace(sb.ToString(), @"\n{2,}", "\n").Trim();
    }

    [GeneratedRegex(@"maxroll\.gg/d4/planner/([a-z0-9]+)(?:#(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex PlannerLink();

    [GeneratedRegex(@"^[a-z0-9]{6,12}$")]
    private static partial Regex BareId();
}
