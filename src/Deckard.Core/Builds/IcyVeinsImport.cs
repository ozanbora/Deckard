using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Deckard.Core.Items;

namespace Deckard.Core.Builds;

/// <summary>
/// Builds from Icy Veins' D4 planner (icy-veins.com/d4/planner/?uid=&lt;id&gt;), read from the build API its pages
/// call (icy-veins.com/api/d4-builds/&lt;id&gt;/). Guide pages sit behind a bot check that apps can't pass, so a guide
/// link is turned down with a pointer to the guide's planner link. The API uses the game's own item keys, affix ids
/// and skill keys; skill points are by index into the class's tree in game order, as the catalog lists it.
/// </summary>
public static partial class IcyVeinsImport
{
    private const string Api = "https://www.icy-veins.com/api/d4-builds/";

    public static string? ParseLink(string link) => PlannerLink().Match(link) is { Success: true } m ? m.Groups[1].Value : null;

    public static bool IsGuideLink(string link) => link.Contains("icy-veins.com/d4/", StringComparison.OrdinalIgnoreCase);

    public static async Task<BuildPlan> LoadAsync(string uid, AffixCatalog catalog, HttpClient http, CancellationToken ct) =>
        Parse(await http.GetStringAsync($"{Api}{Uri.EscapeDataString(uid)}/", ct), catalog);

    /// <summary>Parses the API's response: gear, skills and paragon each hold one set per variant, matched by id.</summary>
    public static BuildPlan Parse(string json, AffixCatalog catalog)
    {
        var build = JsonNode.Parse(json)?["build"] as JsonObject ?? throw new FormatException("Not an Icy Veins build.");
        var names = new ImportNames(catalog);
        JsonObject? Set(string section, string id) =>
            Sets(build, section).FirstOrDefault(s => Text(s["id"]) == id);

        var variants = Sets(build, "gear_data").Select(gear =>
        {
            var id = Text(gear["id"]);
            var className = catalog.Classes.FirstOrDefault(c => string.Equals(c, Text(gear["class"]), StringComparison.OrdinalIgnoreCase)) ?? Text(gear["class"]);
            var skills = Set("skills_data", id);
            return new BuildVariant(Text(gear["name"]) is { Length: > 0 } n ? n : "Build", names.ClassIndex(className), 0,
                Items(gear, names), Bar(skills), Points(skills, className, catalog), Text(gear["notes"]),
                Paragon(Set("paragon_data", id)?["paragonBoard"] as JsonObject, className, names));
        }).ToList();
        return new BuildPlan(Text(build["uid"]), Text(build["name"]).Trim(), catalog.Season, variants, 0);
    }

    private static Dictionary<int, PlannedItem> Items(JsonObject gear, ImportNames names)
    {
        var items = new Dictionary<int, PlannedItem>();
        foreach (var (slotName, keyNode) in gear["itemIds"] as JsonObject ?? new JsonObject())
        {
            var slot = ImportNames.Slot(slotName);
            if (slot < 0 || Text(keyNode) is not { Length: > 0 } key)
                continue;
            // A legendary's base: Icy Veins names its type in equippedGear ("Sword", "Chest Armor").
            var type = Text(gear["equippedGear"]?[slotName]);
            var itemKey = names.Base(key, names.Catalog.Items.Values.Any(i => i.Type == type) ? type : ImportNames.SlotType(slot));
            var power = Text(gear["itemAspects"]?[slotName]) is { Length: > 0 } aspect ? names.Power(aspect)
                : names.Catalog.Items.TryGetValue(itemKey, out var info) ? names.Power(info.Name) : null; // a unique's or mythic's power
            var affixes = (gear["itemUserAffixes"]?[slotName] as JsonArray ?? new JsonArray())
                .Select(a => names.Affix(Text(a?["affixId"]))).OfType<PlannedAffix>().ToList();
            var tempered = names.Affix(Text(gear["itemTemperingAffix"]?[slotName]?["affixId"])) is { } t ? new List<PlannedAffix> { t } : [];
            var sockets = (gear["itemSockets"]?[slotName] as JsonArray ?? new JsonArray())
                .Select(s => names.Socket(Text(s))).OfType<string>().ToList();
            var itemPower = gear["itemPowers"]?[slotName] is JsonValue p && p.TryGetValue<int>(out var ip) ? ip : 0;
            items[slot] = new PlannedItem(slot, itemKey, affixes, tempered, power is null ? [] : [ImportNames.Planned(power)], itemPower, sockets);
        }
        return items;
    }

    private static List<string> Bar(JsonObject? skills) =>
        [.. (skills?["equippedSkills"] as JsonObject ?? new JsonObject()).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Text(p.Value)).Where(s => s.Length > 0)];

    /// <summary>Points spent per node, by the node's index in the class's tree.</summary>
    private static List<SkillStep> Points(JsonObject? skills, string className, AffixCatalog catalog)
    {
        var nodes = catalog.SkillTrees.GetValueOrDefault(className)?.Nodes;
        if (nodes is null || skills?["skillPoints"]?["skillPointDistribution"] is not JsonObject spent)
            return [];
        var ranks = new Dictionary<int, int>();
        foreach (var (index, value) in spent)
            if (int.TryParse(index, out var i) && i >= 0 && i < nodes.Count && value?["spentPoints"] is JsonValue v && v.TryGetValue<int>(out var points) && points > 0)
                ranks[nodes[i].Node] = points;
        return ranks.Count == 0 ? [] : [new SkillStep("Skill points", ranks)];
    }

    /// <summary>
    /// Boards sit at board-grid (col, row), rows growing downward, turned in degrees clockwise. Active cells are
    /// "&lt;board slug&gt;-&lt;row&gt;-&lt;col&gt;", counted from 1 on the unrotated board; the start board's have no slug.
    /// </summary>
    private static List<ParagonStep> Paragon(JsonObject? paragon, string className, ImportNames names)
    {
        if (paragon is null)
            return [];
        var boards = new List<(string Slug, int X, int Y, int Rotation)> { ("", 0, 0, 0) };
        foreach (var (slug, b) in paragon["connectedBoards"] as JsonObject ?? new JsonObject())
            boards.Add((slug, Int(b?["position"]?["col"]), Int(b?["position"]?["row"]), Int(b?["rotation"])));

        var active = (paragon["nodeStates"] as JsonObject ?? new JsonObject())
            .Where(p => Text(p.Value?["state"]) == "active").Select(p => Cell().Match(p.Key)).Where(m => m.Success).ToList();
        var glyphs = (paragon["equippedGlyphs"] as JsonObject ?? new JsonObject())
            .Select(p => (Match: Cell().Match(p.Key), Glyph: p.Value)).Where(g => g.Match.Success).ToList();

        var placed = new List<PlacedBoard>();
        foreach (var (slug, x, y, rotation) in boards)
        {
            if (names.Board(className, slug, start: slug.Length == 0) is not { } hit)
                continue;
            var (id, board) = hit;
            var nodes = active.Where(m => m.Groups[1].Value == slug)
                .Select(m => (int.Parse(m.Groups[2].Value) - 1) * board.Width + int.Parse(m.Groups[3].Value) - 1)
                .Where(i => i >= 0 && i < board.Nodes.Count).ToHashSet();
            var glyph = glyphs.FirstOrDefault(g => g.Match.Groups[1].Value == slug).Glyph;
            placed.Add(new PlacedBoard(id, rotation / 90 % 4, x, y,
                Text(glyph?["id"]) is { Length: > 0 } g ? names.Glyph(g) : null, Int(glyph?["level"]), nodes));
        }
        return placed.Count == 0 ? [] : [new ParagonStep("Paragon", placed)];
    }

    private static IEnumerable<JsonObject> Sets(JsonObject build, string section) =>
        (build[section]?["sets"] as JsonArray ?? new JsonArray()).OfType<JsonObject>();

    private static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static int Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

    [GeneratedRegex(@"icy-veins\.com/d4/planner/?\S*?[?&]uid=([a-z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PlannerLink();

    /// <summary>"demonic-spicules-13-13", or "14-11" on the start board.</summary>
    [GeneratedRegex(@"^(?:([a-z][a-z-]*)-)?(\d+)-(\d+)$")]
    private static partial Regex Cell();
}
