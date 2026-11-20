using Deckard.Core.Items;

/// <summary><c>deckard affix "&lt;line&gt;" [--season n] [--data dir]</c>: shows which affixes a tooltip line matches.</summary>
internal static class AffixCommand
{
    public static int Run(string[] args)
    {
        string? line = null, dataRoot = null;
        int? season = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--season" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n): season = n; i++; break;
                case "--data" when i + 1 < args.Length: dataRoot = args[++i]; break;
                default: line = line is null ? args[i] : $"{line} {args[i]}"; break;
            }
        }
        if (string.IsNullOrWhiteSpace(line))
        {
            Console.Error.WriteLine("usage: deckard affix \"<tooltip line>\" [--season <n>] [--data <dir>]");
            return 2;
        }

        dataRoot ??= AffixCatalog.FindDataRoot();
        AffixCatalog catalog;
        try
        {
            catalog = AffixCatalog.LoadSeason(dataRoot ?? Path.Combine("data", "d4"), season);
        }
        catch (Exception e) when (e is FileNotFoundException or InvalidDataException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        Console.WriteLine($"Season {catalog.Season} (game build {catalog.GameBuild})");
        var matches = catalog.Match(line);
        if (matches.Count == 0)
        {
            Console.WriteLine("No match.");
            return 1;
        }
        foreach (var m in matches)
        {
            var label = m.Entry.Name ?? string.Join(", ", m.Entry.Keys.Take(2));
            var names = m.Entry.Prefix is not null || m.Entry.Suffix is not null ? $"  [{m.Entry.Prefix} / {m.Entry.Suffix}]" : "";
            Console.WriteLine($"  {m.Entry.Kind,-9} {m.Entry.Lines[m.LineIndex]}  =  {string.Join(", ", m.Values)}  ({label}){names}");
        }
        return 0;
    }
}
