using System.Globalization;

namespace Deckard.Core.Trade;

/// <summary>Gold amounts the way players write them: "750k", "25M", "2.5B", "1,000,000".</summary>
public static class Gold
{
    public static long? Parse(string? text)
    {
        var s = (text ?? "").Trim().Replace(",", "").Replace("_", "").ToLowerInvariant();
        if (s.EndsWith("gold", StringComparison.Ordinal))
            s = s[..^4].TrimEnd();
        if (s.Length == 0)
            return null;
        var factor = s[^1] switch { 'k' => 1e3, 'm' => 1e6, 'b' => 1e9, _ => 1 };
        if (factor > 1)
            s = s[..^1];
        if (!double.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) || number <= 0)
            return null;
        var gold = number * factor;
        return gold < 1 || gold > long.MaxValue / 2 ? null : (long)Math.Round(gold);
    }

    public static string Format(long gold) => gold switch
    {
        >= 1_000_000_000 => Short(gold / 1e9) + "B",
        >= 1_000_000 => Short(gold / 1e6) + "M",
        >= 1_000 => Short(gold / 1e3) + "k",
        _ => gold.ToString(CultureInfo.InvariantCulture),
    };

    private static string Short(double value) => value.ToString(value < 10 ? "0.##" : value < 100 ? "0.#" : "0", CultureInfo.InvariantCulture);
}
