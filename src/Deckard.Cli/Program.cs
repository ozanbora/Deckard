using System.Diagnostics;
using Deckard.Capture;
using Deckard.Core.Capture;
using Deckard.Perception.Imaging;
using Deckard.Perception.Tooltips;

if (args is ["affix", .. var affixArgs])
    return AffixCommand.Run(affixArgs);
if (args is ["tooltip", .. var tooltipArgs])
    return await TooltipCommand.RunAsync(tooltipArgs);
if (args is ["zone", .. var zoneArgs])
    return await ZoneCommand.RunAsync(zoneArgs);
if (args is ["points", .. var pointsArgs])
    return await PointsCommand.RunAsync(pointsArgs);

// deckard capture [--process <name>] [--title <text>] [--seconds <n>] [--snapshot-every <seconds>] [--tooltips [--crop]] [--out <dir>] [--border] [--cursor]
CaptureArgs? options;
try
{
    options = CaptureArgs.Parse(args);
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    options = null;
}
if (options is null)
{
    Console.Error.WriteLine("""
        usage: deckard capture [--process <name>] [--title <text>] [--seconds <n>]
                               [--snapshot-every <seconds> | --tooltips [--crop]] [--out <dir>] [--border] [--cursor]
               deckard affix "<tooltip line>" [--season <n>] [--data <dir>]
               deckard tooltip <image.png | folder>... [--season <n>] [--data <dir>] [--save-crops <dir>]

          --process          process to capture (default: "Diablo IV")
          --title            capture the first visible window whose title contains this text instead
          --seconds          stop after n seconds (default: run until Ctrl+C)
          --snapshot-every   save a PNG every n seconds, e.g. to collect detector training data
          --tooltips         save a PNG only when a new item tooltip appears (once per tooltip)
          --crop             with --tooltips, save just the tooltip instead of the whole frame
          --out              snapshot folder (default: ./captures)
        """);
    return 2;
}

var hwnd = options.Title is not null
    ? WindowLocator.FindByTitle(options.Title)
    : WindowLocator.FindByProcessName(options.Process);
if (hwnd == 0)
{
    Console.Error.WriteLine(options.Title is not null
        ? $"No visible window with \"{options.Title}\" in its title."
        : $"No window found for process \"{options.Process}\". Is the game running?");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
if (options.Seconds is { } seconds)
    cts.CancelAfter(TimeSpan.FromSeconds(seconds));

await using var source = await WgcFrameSource.CreateForWindowAsync(hwnd, new WgcOptions { ShowBorder = options.Border, CaptureCursor = options.Cursor });
Console.WriteLine($"Capturing \"{source.DisplayName}\" (0x{hwnd:X}). Ctrl+C to stop.");
source.Start();

if (options.SnapshotEvery is not null || options.Tooltips)
    Directory.CreateDirectory(options.OutputDirectory);
if (options.Tooltips)
    Console.WriteLine("Saving a snapshot whenever a new item tooltip appears.");

var fpsClock = Stopwatch.StartNew();
Stopwatch? sinceSnapshot = null;
var watcher = new TooltipWatcher(new TooltipLocator());
var sinceTooltipCheck = Stopwatch.StartNew();
var tooltipsSaved = 0;
var framesThisSecond = 0;
var firstFrame = true;
try
{
    await foreach (var frame in source.Frames.ReadAllAsync(cts.Token))
    {
        using (frame)
        {
            framesThisSecond++;
            if (firstFrame)
            {
                Console.WriteLine($"First frame: {frame.Width}x{frame.Height}");
                firstFrame = false;
            }

            if (options.SnapshotEvery is { } every && (sinceSnapshot is null || sinceSnapshot.Elapsed.TotalSeconds >= every))
            {
                sinceSnapshot = Stopwatch.StartNew();
                var path = Path.Combine(options.OutputDirectory, $"capture_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
                await FrameFile.SavePngAsync(frame, path);
                Console.WriteLine($"Saved {path}");
            }

            // Locating takes ~8 ms at 3440x1440; four checks a second catch every hover without loading the CPU.
            if (options.Tooltips && sinceTooltipCheck.ElapsedMilliseconds >= 250)
            {
                sinceTooltipCheck.Restart();
                var tooltips = watcher.Check(frame);
                if (tooltips.Count > 0)
                {
                    var stamp = $"tooltip_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
                    if (options.Crop)
                    {
                        for (var i = 0; i < tooltips.Count; i++)
                        {
                            using var crop = frame.Crop(tooltips[i]);
                            await FrameFile.SavePngAsync(crop, Path.Combine(options.OutputDirectory, $"{stamp}_{i}.png"));
                        }
                    }
                    else
                    {
                        await FrameFile.SavePngAsync(frame, Path.Combine(options.OutputDirectory, $"{stamp}.png"));
                    }
                    tooltipsSaved += tooltips.Count;
                    Console.WriteLine($"Saved {stamp} ({tooltips.Count} tooltip(s) at {string.Join(", ", tooltips.Select(r => $"{r.X},{r.Y}"))}); {tooltipsSaved} so far");
                }
            }
        }

        if (!options.Tooltips && fpsClock.ElapsedMilliseconds >= 1000)
        {
            Console.WriteLine($"{framesThisSecond / fpsClock.Elapsed.TotalSeconds:F1} fps");
            framesThisSecond = 0;
            fpsClock.Restart();
        }
    }
    Console.WriteLine("Capture target closed.");
}
catch (OperationCanceledException)
{
}
return 0;

internal sealed record CaptureArgs
{
    public string Process { get; init; } = WindowLocator.Diablo4ProcessName;
    public string? Title { get; init; }
    public double? Seconds { get; init; }
    public double? SnapshotEvery { get; init; }
    public string OutputDirectory { get; init; } = "captures";
    public bool Tooltips { get; init; }
    public bool Crop { get; init; }
    public bool Border { get; init; }
    public bool Cursor { get; init; }

    public static CaptureArgs? Parse(string[] args)
    {
        if (args.Length == 0 || args[0] != "capture")
            return null;

        var result = new CaptureArgs();
        for (var i = 1; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            double? NextNumber() => double.TryParse(Next(), out var n) && n > 0 ? n : null;

            switch (args[i])
            {
                case "--process": result = result with { Process = Next() ?? result.Process }; break;
                case "--title": result = result with { Title = Next() }; break;
                case "--seconds": result = result with { Seconds = NextNumber() ?? throw Invalid("--seconds") }; break;
                case "--snapshot-every": result = result with { SnapshotEvery = NextNumber() ?? throw Invalid("--snapshot-every") }; break;
                case "--out": result = result with { OutputDirectory = Next() ?? result.OutputDirectory }; break;
                case "--tooltips": result = result with { Tooltips = true }; break;
                case "--crop": result = result with { Crop = true }; break;
                case "--border": result = result with { Border = true }; break;
                case "--cursor": result = result with { Cursor = true }; break;
                default: return null;
            }
        }
        if (result.Crop && !result.Tooltips)
            throw new ArgumentException("--crop only works together with --tooltips.");
        return result;
    }

    private static ArgumentException Invalid(string flag) => new($"{flag} needs a positive number.");
}
