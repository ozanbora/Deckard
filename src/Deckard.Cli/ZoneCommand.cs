using Deckard.Perception.Imaging;
using Deckard.Perception.Ocr;
using Deckard.Perception.Zones;

/// <summary>deckard zone &lt;image&gt;...: the zone name read from game screenshots, with the strip that was read.</summary>
internal static class ZoneCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: deckard zone <image> [<image>...] [--save-strip]");
            return 2;
        }
        var save = args.Contains("--save-strip");
        var ocr = TextRecognizer.Create();
        foreach (var path in args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)))
        {
            using var frame = await FrameFile.LoadAsync(path);
            var region = ZoneReader.Region(frame.Width, frame.Height);
            using var strip = frame.Crop(region);
            using var prepared = ImageOps.PrepareForOcr(strip, scale: 2);
            var raw = string.Join(" | ", (await ocr.RecognizeAsync(prepared)).Select(l => l.Text));
            Console.WriteLine($"{path} ({frame.Width}x{frame.Height}, strip {region}): read \"{raw}\" -> zone {ZoneReader.Parse(raw) ?? "(none)"}");
            if (save)
                await FrameFile.SavePngAsync(prepared, Path.ChangeExtension(path, ".zone.png"));
        }
        return 0;
    }
}
