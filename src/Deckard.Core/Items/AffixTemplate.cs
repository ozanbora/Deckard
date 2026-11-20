using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Deckard.Core.Items;

/// <summary>
/// One tooltip line template such as <c>+#% Critical Strike Chance</c>, where <c>#</c> stands for a rolled number.
/// Compiles to a regex that tolerates common OCR noise: a missing <c>+</c>, extra spaces, thousands separators,
/// the multiplicative marker (<c>x</c>, <c>×</c>, <c>[x]</c>) and the advanced-tooltip roll range (<c>[12.5 - 18]</c>).
/// </summary>
public sealed partial class AffixTemplate
{
    private const string Number = @"(\d[\d,]*(?:\.\d+)?)";

    private readonly Regex _regex;

    public AffixTemplate(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        Text = text;
        var (pattern, slots) = BuildPattern(Normalize(text));
        ValueCount = slots;
        _regex = new Regex($"^{pattern}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public string Text { get; }

    /// <summary>Number of rolled values (<c>#</c>) in the line.</summary>
    public int ValueCount { get; }

    /// <summary>Matches an already <see cref="Normalize">normalized</see> line and returns its rolled values.</summary>
    public bool TryMatchNormalized(string normalizedLine, out double[] values)
    {
        var match = _regex.Match(normalizedLine);
        if (!match.Success)
        {
            values = [];
            return false;
        }

        values = new double[ValueCount];
        for (var i = 0; i < ValueCount; i++)
            values[i] = double.Parse(match.Groups[i + 1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture);
        return true;
    }

    public bool TryMatch(string line, out double[] values) => TryMatchNormalized(Normalize(line), out values);

    /// <summary>
    /// Brings OCR text and templates to one form: drops roll ranges and multiplicative markers, unifies dashes
    /// and quotes, and collapses whitespace. Signs are kept; the template regex makes them optional.
    /// </summary>
    public static string Normalize(string line)
    {
        var text = ComparisonDelta().Replace(FixOcrLetters(line), " ");
        text = ClassRestriction().Replace(text, " ");
        text = RangeAnnotation().Replace(text, " ");
        text = TrailingParenthesis().Replace(text, "");
        text = text.Replace("[x]", " ", StringComparison.OrdinalIgnoreCase).Replace("[+]", " ").Replace('×', ' ')
            .Replace('’', '\'').Replace('‘', '\'').Replace('–', '-').Replace('—', '-');
        text = DanglingBracket().Replace(text, " "); // a range that wrapped to the next line leaves its "[" behind
        text = MultiplicativeSign().Replace(text, "");
        text = SpacedThousands().Replace(text, "$1,$2"); // OCR: "+1, 110" or "1 ,500" -> "+1,110", "1,500"
        text = text.Trim().TrimStart('•', '◆', '*', '-', ' ');
        return Whitespace().Replace(text, " ").Trim();
    }

    /// <summary>
    /// The tooltip font's 1 and l look alike, so OCR reads "Kill" as "Ki11" and "All" as "A11". A 1 (or |) right after
    /// a letter is an l; numbers are left alone, and so is a 1 after x, the multiplier sign ("x15%").
    /// </summary>
    public static string FixOcrLetters(string text)
    {
        string fixedText;
        while ((fixedText = LetterOne().Replace(text, "l")) != text)
            text = fixedText; // "Ki11": each pass turns the next 1 into an l
        return text;
    }

    [GeneratedRegex(@"(?<=[A-WYZa-wyz])[1|]")]
    private static partial Regex LetterOne();

    [GeneratedRegex(@"\[(?!\s*\d)")]
    private static partial Regex DanglingBracket();

    private static (string Pattern, int Slots) BuildPattern(string template)
    {
        var pattern = new StringBuilder();
        var slots = 0;
        var literal = new StringBuilder();

        void FlushLiteral()
        {
            if (literal.Length == 0)
                return;
            // Escape word by word: Regex.Escape would turn spaces into "\ ", and OCR may merge or split words anyway.
            pattern.Append(string.Join(@"\s*", Whitespace().Split(literal.ToString()).Select(Regex.Escape)));
            literal.Clear();
        }

        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '+' && i + 1 < template.Length && template[i + 1] == '#')
                continue; // the sign before a number is optional; OCR often loses it
            if (c != '#')
            {
                literal.Append(c);
                continue;
            }

            FlushLiteral();
            pattern.Append(@"\+?\s*").Append(Number);
            slots++;
            if (i + 1 < template.Length && template[i + 1] == '%')
            {
                pattern.Append(@"\s*%");
                i++;
            }
        }
        FlushLiteral();
        return (pattern.ToString(), slots);
    }

    // Advanced-tooltip roll info after a value: "[541 - 740]", "[7.0 - 11.0]%", or a single fixed value "+[100]".
    // OCR often reads the closing bracket as 1, l or |, or drops it ("[7.0 - 11.01%", "[6 -"), so the range runs over
    // any digits, separators and dashes and the closing character is optional. "[x]" is left alone (no digit).
    [GeneratedRegex(@"\+?\[\s*[+\-]?\d[\d.,\s\-+%]*[\]|lI]?\s*%?")]
    private static partial Regex RangeAnnotation();

    // "x12%" (multiplicative) -> "12%"; only when the x sits directly before a number.
    [GeneratedRegex(@"(?<![A-Za-z])[xX]\s*(?=[+\d#])")]
    private static partial Regex MultiplicativeSign();

    // Comparison view: the difference to the equipped item, "(+8%)", "( +24.0%)", "(-21.4% Toughness)", "(-2146)".
    [GeneratedRegex(@"\(\s*[+\-]\s*\d[\d,.\s]*%?(?:\s*[A-Za-z]+)?\s*\)")]
    private static partial Regex ComparisonDelta();

    // Class-restricted affix: "(Barbarian Only)"; the class is often an icon that OCR reads as a symbol.
    [GeneratedRegex(@"\([^()]{0,20}\bOnly\)", RegexOptions.IgnoreCase)]
    private static partial Regex ClassRestriction();

    // A delta that wrapped to the next line leaves a lone "(" at the end.
    [GeneratedRegex(@"\s*\(\s*$")]
    private static partial Regex TrailingParenthesis();

    [GeneratedRegex(@"(\d)(?:,\s+|\s+,\s*)(\d{3})(?!\d)")]
    private static partial Regex SpacedThousands();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
