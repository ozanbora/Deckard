using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Deckard.Core.Items;

namespace Deckard.Core.Builds;

/// <summary>
/// Builds from d4builds.gg (d4builds.gg/builds/&lt;name&gt;/, "?var=n" picks a variant). The site is a Gatsby app: a
/// build page's page-data.json names the build's document, which the page reads from the site's Firestore over its
/// public REST API, as this does. Builds name everything (items, aspects, affixes, boards) in English, matched
/// against the catalog by <see cref="ImportNames"/>. The skill tree is left out: d4builds encodes it in its own node
/// order, which only its encrypted data files give.
/// </summary>
public static partial class D4BuildsImport
{
    private const string Site = "https://d4builds.gg";
    private const string Documents = "https://firestore.googleapis.com/v1/projects/d4builds-a3254/databases/(default)/documents/builds/";

    /// <summary>The build page's path and the variant asked for, from a d4builds link.</summary>
    public static (string Path, int? Variant)? ParseLink(string link)
    {
        var m = BuildLink().Match(link);
        if (!m.Success)
            return null;
        var variant = VariantQuery().Match(link);
        return ($"/builds/{m.Groups[1].Value}/", variant.Success ? int.Parse(variant.Groups[1].Value) : null);
    }

    public static async Task<BuildPlan> LoadAsync(string path, int? variant, AffixCatalog catalog, HttpClient http, CancellationToken ct)
    {
        using var page = JsonDocument.Parse(await http.GetStringAsync($"{Site}/page-data{path}page-data.json", ct));
        // Guide pages name their build's document; players' own builds (/builds/<uuid>/) share one page and are that document.
        var id = (page.RootElement.GetProperty("result").GetProperty("pageContext").TryGetProperty("seoId", out var seo) ? seo.GetString() : null)
            ?? (Uuid().IsMatch(path.Trim('/').Split('/')[^1]) ? path.Trim('/').Split('/')[^1] : null)
            ?? throw new FormatException("The d4builds page names no build.");
        return Parse(await http.GetStringAsync(Documents + Uri.EscapeDataString(id), ct), catalog, variant, id);
    }

    /// <summary>Parses a build's Firestore document. The document is the first variant; its "variants" are the others.</summary>
    public static BuildPlan Parse(string documentJson, AffixCatalog catalog, int? variant = null, string id = "")
    {
        var fields = JsonNode.Parse(documentJson)?["fields"] ?? throw new FormatException("Not a d4builds build.");
        var doc = (JsonObject)Plain(new JsonObject { ["mapValue"] = new JsonObject { ["fields"] = fields.DeepClone() } })!;
        var names = new ImportNames(catalog);
        var all = new List<JsonObject> { doc };
        if (doc["variants"] is JsonArray more)
            all.AddRange(more.OfType<JsonObject>().OrderBy(v => Int(v["index"]) ?? 0));
        var variants = all.Select(v => Variant(v, names)).ToList();
        return new BuildPlan(id, Text(doc["name"]).Trim(), Int(doc["season"]) ?? catalog.Season, variants,
            Math.Clamp(variant ?? 0, 0, Math.Max(0, variants.Count - 1)));
    }

    private static BuildVariant Variant(JsonObject v, ImportNames names)
    {
        var className = Text(v["class"]);
        var items = new Dictionary<int, PlannedItem>();
        foreach (var (slotName, gear) in v["gear"] as JsonObject ?? new JsonObject())
        {
            var slot = ImportNames.Slot(slotName);
            if (slot < 0 || Text(gear) is not { Length: > 0 } name)
                continue;
            var type = ImportNames.SlotType(slot);
            var power = names.Power(name);
            var key = power?.Kind == AffixKind.Aspect ? names.Base(null, type) : names.Item(name, type) ?? names.Base(null, type);
            var greater = Strings(v["greaterAffixes"]?[slotName]);
            var affixes = Strings(v["newStats"]?[slotName])
                .Select((stat, i) => stat is null ? null : names.Line(stat, AffixKind.Affix, type) is { } e ? ImportNames.Planned(e, greater.ElementAtOrDefault(i) == "1") : null)
                .OfType<PlannedAffix>().ToList();
            // Tempering is written "Maximum Life (Worldly Endurance - Defensive)": the affix, then its recipe.
            var tempered = Strings(v["temperingStats"]?[slotName]).OfType<string>()
                .Select(t => names.Line(t.Split(" (")[0], AffixKind.Tempered, type)).OfType<AffixEntry>().Select(e => ImportNames.Planned(e)).ToList();
            var sockets = Strings(v["newGems"]?[slotName]).OfType<string>().Select(names.Socket).OfType<string>().ToList();
            items[slot] = new PlannedItem(slot, key, affixes, tempered, power is null ? [] : [ImportNames.Planned(power)], 0, sockets);
        }

        // The seal and charms: named charms in order, each with its affixes; the seal has only affixes.
        if (Strings(v["sealStats"]).OfType<string>().ToList() is { Count: > 0 } seal)
            items[20] = new PlannedItem(20, names.Base(null, "Horadric Seal"), Affixes(seal, names), [], []);
        var charms = v["charms"] as JsonArray ?? new JsonArray();
        for (var i = 0; i < charms.Count && i < 6; i++)
        {
            var name = charms[i] is JsonObject o ? Text(o["name"]) : Text(charms[i]);
            if (names.Item(name, "Charm") is { } charm)
                items[21 + i] = new PlannedItem(21 + i, charm, Affixes(Strings(v["charmStats"]?[$"{i}"]).OfType<string>(), names), [], []);
        }

        var bar = (v["skills"] as JsonArray ?? new JsonArray()).Select(s => names.Skill(className, Text(s))).OfType<string>().ToList();
        return new BuildVariant(Text(v["variantName"]) is { Length: > 0 } n ? n : "Build", names.ClassIndex(className), 0, items, bar, [],
            Notes(Text(v["notes"])), Paragon(v["paragon"] as JsonObject, className, names));
    }

    private static List<PlannedAffix> Affixes(IEnumerable<string> lines, ImportNames names) =>
        [.. lines.Select(l => names.Line(l, null)).OfType<AffixEntry>().Select(e => ImportNames.Planned(e))];

    /// <summary>
    /// Boards are placed by board-grid x and y (y up) and turned in degrees, clockwise; allocated cells are named
    /// "&lt;board name in letters&gt;_r&lt;row&gt;c&lt;column&gt;", counted from 1 on the unrotated board.
    /// </summary>
    private static List<ParagonStep> Paragon(JsonObject? paragon, string className, ImportNames names)
    {
        if (paragon?["boards"] is not JsonArray boards)
            return [];
        var tiles = Strings(paragon["selectedTiles"]).OfType<string>().Select(t => Tile().Match(t)).Where(m => m.Success).ToList();
        var placed = new List<PlacedBoard>();
        foreach (var b in boards.OfType<JsonObject>())
        {
            var name = Text(b["name"]);
            int x = Int(b["x"]) ?? 0, y = Int(b["y"]) ?? 0;
            if (names.Board(className, name, start: x == 0 && y == 0) is not { } hit)
                continue;
            var (id, board) = hit;
            var prefix = ImportNames.Letters(name);
            var nodes = tiles.Where(m => m.Groups[1].Value == prefix)
                .Select(m => (int.Parse(m.Groups[2].Value) - 1) * board.Width + int.Parse(m.Groups[3].Value) - 1)
                .Where(i => i >= 0 && i < board.Nodes.Count).ToHashSet();
            placed.Add(new PlacedBoard(id, (Int(b["rotation"]) ?? 0) / 90 % 4, x, -y,
                Text(b["glyph"]) is { Length: > 0 } glyph ? names.Glyph(glyph) : null, Int(b["glyphLevel"]) ?? 0, nodes));
        }
        return placed.Count == 0 ? [] : [new ParagonStep("Paragon", placed)];
    }

    /// <summary>Notes link with "#[url][text]#"; keep the text.</summary>
    private static string Notes(string notes) => NoteLink().Replace(notes, "$1").Trim();

    /// <summary>Firestore's typed values ({"stringValue": …}, {"mapValue": {"fields": …}}) as plain JSON.</summary>
    private static JsonNode? Plain(JsonNode? value)
    {
        if (value is not JsonObject { Count: 1 } typed)
            return null;
        var (kind, inner) = typed.First();
        return kind switch
        {
            "mapValue" => new JsonObject((inner?["fields"] as JsonObject ?? new JsonObject()).Select(p => KeyValuePair.Create(p.Key, Plain(p.Value)))),
            "arrayValue" => new JsonArray([.. (inner?["values"] as JsonArray ?? new JsonArray()).Select(Plain)]),
            "integerValue" => long.TryParse(Text(inner), out var n) ? JsonValue.Create(n) : null,
            "nullValue" => null,
            _ => inner?.DeepClone(),
        };
    }

    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var n) ? (int)n : null;

    private static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static List<string?> Strings(JsonNode? node) =>
        node is JsonArray a ? [.. a.Select(x => x is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : null)] : [];

    [GeneratedRegex(@"d4builds\.gg/builds/([a-z0-9-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BuildLink();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex Uuid();

    [GeneratedRegex(@"[?&]var=(\d+)")]
    private static partial Regex VariantQuery();

    [GeneratedRegex(@"^([a-z]+)_r(\d+)c(\d+)$")]
    private static partial Regex Tile();

    [GeneratedRegex(@"#\[[^\]]*\]\[([^\]]*)\]#")]
    private static partial Regex NoteLink();
}
