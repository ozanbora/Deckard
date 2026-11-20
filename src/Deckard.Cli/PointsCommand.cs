using Deckard.Perception.Builds;
using Deckard.Perception.Imaging;
using Deckard.Perception.Ocr;

/// <summary>deckard points &lt;image&gt;...: the skill tree's or paragon screen's points spent and available, with what OCR read.</summary>
internal static class PointsCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: deckard points <image> [<image>...] [--save-strip]");
            return 2;
        }
        var save = args.Contains("--save-strip");
        var ocr = TextRecognizer.Create();
        foreach (var path in args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)))
        {
            using var frame = await FrameFile.LoadAsync(path);
            using var strip = frame.Crop(PointsSpent.Panel(frame.Height));
            using var prepared = ImageOps.PrepareForOcr(strip, scale: 2);
            var lines = (await ocr.RecognizeAsync(prepared)).Select(l => l.Text).ToList();
            Console.WriteLine($"{path}: read \"{string.Join(" | ", lines)}\" -> {await PointsSpent.ReadAsync(frame, ocr)}");
            if (save)
            {
                await FrameFile.SavePngAsync(prepared, Path.ChangeExtension(path, ".points.png"));
                using var count = frame.Crop(PointsSpent.Count(frame.Height));
                await FrameFile.SavePngAsync(count, Path.ChangeExtension(path, ".count.png"));
            }
        }
        return 0;
    }
}
