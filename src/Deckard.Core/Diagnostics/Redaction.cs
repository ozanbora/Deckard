namespace Deckard.Core.Diagnostics;

/// <summary>What may leave the player's PC from an error: Deckard's own stack frames, and text without their user folder.</summary>
public static class Redaction
{
    /// <summary>
    /// Deckard's own frames of a stack trace (type and method, no file paths or line numbers), for telemetry: what
    /// crashed, without anything about the player's PC.
    /// </summary>
    public static string OwnFrames(string? stackTrace, int maxChars = 2000)
    {
        var frames = (stackTrace ?? "").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("at Deckard.", StringComparison.Ordinal))
            .Select(line => line.Split(" in ", 2)[0]); // drops " in C:\...\File.cs:line 12"
        var text = string.Join("\n", frames);
        return text.Length > maxChars ? text[..maxChars] : text;
    }

    /// <summary>The Windows user's folder (and so their user name) in <paramref name="text"/> replaced by %USERPROFILE%.</summary>
    public static string HideUserFolder(string text, string userFolder) =>
        userFolder.Length > 3 ? text.Replace(userFolder, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase) : text;
}
