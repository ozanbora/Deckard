using Deckard.Core.Capture;
using Deckard.Core.Items;
using Deckard.Perception.Imaging;
using Deckard.Perception.Tooltips;

/// <summary><c>deckard tooltip &lt;image or folder&gt;... [--season n] [--data dir] [--save-crops dir]</c>: reads item tooltips from snapshots.</summary>
internal static class TooltipCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var inputs = new List<string>();
        string? dataRoot = null, cropDir = null;
        int? season = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--season" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n): season = n; i++; break;
                case "--data" when i + 1 < args.Length: dataRoot = args[++i]; break;
                case "--save-crops" when i + 1 < args.Length: cropDir = args[++i]; break;
                default: inputs.Add(args[i]); break;
            }
        }
        var files = inputs.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.png").Order() : (IEnumerable<string>)[p]).ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine("usage: deckard tooltip <image.png | folder>... [--season <n>] [--data <dir>] [--save-crops <dir>]");
            return 2;
        }

        AffixCatalog catalog;
        try
        {
            catalog = AffixCatalog.LoadSeason(dataRoot ?? AffixCatalog.FindDataRoot() ?? Path.Combine("data", "d4"), season);
        }
        catch (Exception e) when (e is FileNotFoundException or InvalidDataException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        if (cropDir is not null)
            Directory.CreateDirectory(cropDir);

        Console.WriteLine($"Season {catalog.Season} (game build {catalog.GameBuild})");
        var reader = new TooltipReader(catalog);
        var found = 0;
        foreach (var file in files)
        {
            using var frame = await FrameFile.LoadAsync(file);
            var tooltips = await reader.ReadAsync(frame);
            // An image saved by capture --tooltips --crop is the tooltip itself: too narrow for a screen to search.
            if (tooltips.Count == 0 && frame.Width < frame.Height)
                tooltips = await reader.ReadAsync(frame, [new PixelRect(0, 0, frame.Width, frame.Height)]);
            for (var index = 0; index < tooltips.Count; index++)
            {
                found++;
                Print(Path.GetFileName(file), tooltips[index]);
                if (cropDir is not null)
                {
                    using var crop = frame.Crop(tooltips[index].Bounds);
                    await FrameFile.SavePngAsync(crop, Path.Combine(cropDir, $"{Path.GetFileNameWithoutExtension(file)}_{index}.png"));
                }
            }
        }
        Console.WriteLine($"{found} tooltip(s) in {files.Count} image(s).");
        return found > 0 ? 0 : 1;
    }

    private static void Print(string file, ItemTooltip t)
    {
        var matched = t.Lines.Count(l => l.IsMatched);
        Console.WriteLine();
        Console.WriteLine($"{file}  at ({t.Bounds.X},{t.Bounds.Y} {t.Bounds.Width}x{t.Bounds.Height})");
        var corrected = t.NameConfidence is { } c && t.RawName != t.Name ? $"   (read as \"{t.RawName}\", {c:P0} match)" : "";
        Console.WriteLine($"  {t.Name ?? "(name not read)"}{corrected}");
        if (t.NamedPower is { } power)
            Console.WriteLine($"  {power.Kind} from name: {power.Name}");
        Console.WriteLine($"  {t.TypeLine ?? "(type line not read)"}   item power {t.ItemPower?.ToString() ?? "?"}, requires level {t.RequiredLevel?.ToString() ?? "?"}");
        foreach (var line in t.Lines)
        {
            if (line.Matches is [var best, ..])
            {
                var entry = best.Entry;
                var also = line.Matches.Count > 1 ? $"  (+{line.Matches.Count - 1} other)" : "";
                Console.WriteLine($"    {entry.Kind,-9} {entry.Lines[best.LineIndex]}  =  {string.Join(", ", best.Values)}{also}{(line.Greater ? "  (greater)" : "")}");
            }
            else
            {
                Console.WriteLine($"    {"?",-9} {line.Text}");
            }
        }
        Console.WriteLine($"  {matched}/{t.Lines.Count} lines matched{(t.AccountBound ? ", account bound" : "")}{(t.Truncated ? ", scrolled (more below)" : "")}");
    }
}
