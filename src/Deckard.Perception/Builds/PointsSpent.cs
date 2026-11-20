using System.Text.RegularExpressions;
using Deckard.Core.Capture;
using Deckard.Perception.Imaging;
using Deckard.Perception.Ocr;

namespace Deckard.Perception.Builds;

/// <summary>
/// How far the character is: the "N points spent" line in the skill tree's and the paragon screen's top-left panel (the
/// same place on both, measured at 3440x1440 around y 170, x 100-450) and the "Available Points" count under it (around
/// y 265, between two icons), and which of a build's leveling steps that makes the one to work toward.
/// </summary>
public static partial class PointsSpent
{
    /// <summary>
    /// The panel's points spent and available, with OCR; null when it isn't on screen or doesn't read. Available is -1
    /// when the count under "Available Points" doesn't read: unknown, not none (the font's 1 is a bare stroke OCR can miss).
    /// </summary>
    public static async Task<(int Spent, int Available)?> ReadAsync(Frame frame, TextRecognizer ocr)
    {
        using var crop = frame.Crop(Panel(frame.Height));
        using var prepared = ImageOps.PrepareForOcr(crop, scale: 2);
        var lines = (await ocr.RecognizeAsync(prepared)).Select(l => l.Text).ToList();
        if (Parse(lines) is (var spent, _) && lines.Any(l => l.Contains("Available", StringComparison.OrdinalIgnoreCase)))
        {
            // Read with the rest, the count counts only in its shape between the icons: a bare number may hold an icon
            // read as 0 ("10" for ◆ 1 ◆, live, with 1 to spend).
            if (Available(lines) is { } read)
                return (spent, read);
            // OCR skips the count when it's read with the rest: a lone digit between two icons. Scaled up with its heading,
            // it reads the icons as 0s ("060" for 6).
            using var count = frame.Crop(Count(frame.Height));
            foreach (var scale in new[] { 4, 8 })
            {
                using var big = ImageOps.PrepareForOcr(count, scale: scale);
                if (Available([.. (await ocr.RecognizeAsync(big)).Select(l => l.Text)]) is { } available)
                    return (spent, available);
            }
            // A lone digit between the icons may not read at all ("◆ 8 ◆": nothing, or "obo"): without the icons it does.
            if (BlankIcons(count))
                foreach (var scale in new[] { 2, 4 })
                {
                    using var big = ImageOps.PrepareForOcr(count, scale: scale);
                    if (Digits([.. (await ocr.RecognizeAsync(big)).Select(l => l.Text)]) is { } available)
                        return (spent, available);
                }
            return (spent, -1);
        }
        return Parse(lines);
    }

    /// <summary>"Available Points" and the count between its icons under it.</summary>
    public static PixelRect Count(int h) => new((int)(h * 0.12), (int)(h * 0.13), (int)(h * 0.15), (int)(h * 0.075));

    /// <summary>
    /// The skill tree docked in the Abilities panel: its box has "Available Points" and the count, no points spent
    /// (measured at 3440x1440: x 2540-2880, y 185-315, the panel being the right 900 px).
    /// </summary>
    public static PixelRect DockedBox(int w, int h) => new(w - (int)(h * 0.625), (int)(h * 0.128), (int)(h * 0.236), (int)(h * 0.09));

    /// <summary>The docked panel's points available, with OCR; null when its box doesn't read.</summary>
    public static async Task<int?> ReadDockedAsync(Frame frame, TextRecognizer ocr)
    {
        using var box = frame.Crop(DockedBox(frame.Width, frame.Height));
        foreach (var scale in new[] { 2, 4, 8 })
        {
            using var big = ImageOps.PrepareForOcr(box, scale: scale);
            if (Available([.. (await ocr.RecognizeAsync(big)).Select(l => l.Text)]) is { } available)
                return available;
        }
        if (BlankIcons(box)) // as on the full tree: the digits alone
            foreach (var scale in new[] { 2, 4 })
            {
                using var big = ImageOps.PrepareForOcr(box, scale: scale);
                if (Digits([.. (await ocr.RecognizeAsync(big)).Select(l => l.Text)]) is { } available)
                    return available;
            }
        return null;
    }

    /// <summary>
    /// The count from the line under "Available Points" read with its icons, "060" for 6; null for any other shape. The
    /// font's 1 is a bare stroke, read as I, l or | ("0I0").
    /// </summary>
    public static int? Available(IReadOnlyList<string> lines)
    {
        foreach (var line in lines.SkipWhile(l => !l.Contains("Available", StringComparison.OrdinalIgnoreCase)).Skip(1))
        {
            var ones = Regex.Replace(line, @"[Il|]", "1");
            if (IconsAround().Match(Regex.Replace(ones, @"[^0-9oO]", "")) is { Success: true } m)
                return int.Parse(m.Groups[1].Value);
            // The left icon read as a mark rather than a 0 (". 10" for ◆ 1 ◆): the digits before the right icon's 0.
            if (MarkThenIcon().Match(ones) is { Success: true } marked)
                return int.Parse(marked.Groups[1].Value);
        }
        return null;
    }

    /// <summary>
    /// Blanks all but the count in a crop of "Available Points" and the count (<see cref="Count"/>, <see cref="DockedBox"/>):
    /// on its lower half (the heading is above), the count is the run of bright columns nearest the middle, as it sits
    /// centred under the heading, with the runs close beside it (its other digits); everything else (the icons either
    /// side, a panel's edge) goes dark. False without a run on each side of it: no icons, no count to tell.
    /// </summary>
    internal static bool BlankIcons(Frame count)
    {
        int top = count.Height / 2, w = count.Width;
        bool Lit(int x)
        {
            var px = count.Pixels;
            for (var y = top; y < count.Height; y++)
            {
                var o = y * count.Stride + x * Frame.BytesPerPixel;
                if (Math.Max(px[o], Math.Max(px[o + 1], px[o + 2])) > 120)
                    return true;
            }
            return false;
        }
        var runs = new List<(int From, int To)>();
        for (var x = 0; x < w; x++)
            if (Lit(x))
            {
                if (runs.Count > 0 && x - runs[^1].To <= 2)
                    runs[^1] = (runs[^1].From, x); // a glyph's own gaps
                else
                    runs.Add((x, x));
            }
        if (runs.Count < 3)
            return false;
        // Digits stand closer together than to the icons (measured at 1440: 3-6 px apart, 13-21 px from the icons).
        var gap = count.Height * 0.08;
        int first = runs.IndexOf(runs.MinBy(r => Math.Abs((r.From + r.To) / 2.0 - w / 2.0))), last = first;
        while (first > 0 && runs[first].From - runs[first - 1].To <= gap)
            first--;
        while (last < runs.Count - 1 && runs[last + 1].From - runs[last].To <= gap)
            last++;
        if (first == 0 || last == runs.Count - 1)
            return false;
        var dark = count.Pixels;
        for (var y = top; y < count.Height; y++)
            for (var x = 0; x < w; x++)
                if (x < runs[first].From || x > runs[last].To)
                {
                    var o = y * count.Stride + x * Frame.BytesPerPixel;
                    dark[o] = dark[o + 1] = dark[o + 2] = 20;
                }
        return true;
    }

    /// <summary>The count read without its icons: the line under "Available Points", digits only (the font's 1 as I, l or |).</summary>
    internal static int? Digits(IReadOnlyList<string> lines)
    {
        foreach (var line in lines.SkipWhile(l => !l.Contains("Available", StringComparison.OrdinalIgnoreCase)).Skip(1))
            if (Regex.Replace(line, @"[Il|]", "1").Trim() is var digits && OnlyDigits().IsMatch(digits))
                return int.Parse(digits);
        return null;
    }

    [GeneratedRegex(@"^\d{1,3}$")]
    private static partial Regex OnlyDigits();

    /// <summary>Where the panel's lines are read.</summary>
    public static PixelRect Panel(int h) => new((int)(h * 0.06), (int)(h * 0.095), (int)(h * 0.26), (int)(h * 0.12));

    /// <summary>The panel's lines: "35 points spent", "Available Points", then the count between its icons.</summary>
    public static (int Spent, int Available)? Parse(IReadOnlyList<string> lines)
    {
        if (lines.Select(Parse).FirstOrDefault(n => n is not null) is not { } spent)
            return null;
        var below = lines.SkipWhile(l => !l.Contains("Available", StringComparison.OrdinalIgnoreCase)).Skip(1);
        var available = below.Select(l => Number().Match(l)).FirstOrDefault(m => m.Success);
        return (spent, available is null ? 0 : int.Parse(available.Value.Replace(",", "")));
    }

    /// <summary>The number in "253 points spent", "1 point spent"; null for anything else.</summary>
    internal static int? Parse(string text) =>
        Line().Match(text) is { Success: true } m ? int.Parse(m.Groups[1].Value.Replace(",", "")) : null;

    /// <summary>
    /// The step to work toward: the first whose points (cumulative, as planners give them) take every point the player has
    /// to spend, so a step only a point away doesn't leave the rest unmarked; with none to spend (or the count unread),
    /// the first not yet spent. Once every step's are spent, the last.
    /// </summary>
    public static int StepFor(IReadOnlyList<int> stepPoints, int spent, int available = 0)
    {
        for (var i = 0; i < stepPoints.Count; i++)
            if (stepPoints[i] >= spent + Math.Max(1, available))
                return i;
        return stepPoints.Count - 1;
    }

    [GeneratedRegex(@"(\d[\d,]*)\s*points?\s+spent", RegexOptions.IgnoreCase)]
    private static partial Regex Line();

    [GeneratedRegex(@"^[0oO](\d+)[0oO]$")]
    private static partial Regex IconsAround();

    [GeneratedRegex(@"^\s*[^\w\s]\s*(\d+?)\s*[0oO]\s*$")]
    private static partial Regex MarkThenIcon();

    [GeneratedRegex(@"\d[\d,]*")]
    private static partial Regex Number();
}
