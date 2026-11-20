using System.Text;
using Deckard.Core.Diagnostics;

namespace Deckard.App;

/// <summary>
/// Errors Deckard didn't expect, written to %LOCALAPPDATA%\Deckard\logs (one file a day, the last 7 days kept), so one
/// failure is logged instead of closing the app. The log stays on the player's PC; bug reports can include its last lines,
/// and telemetry the crash's type and Deckard's own stack frames, both only with the player's choice.
/// </summary>
public static class ErrorLog
{
    private const int KeepDays = 7;
    private const long MaxFileBytes = 1_000_000; // a loop of errors stops at this, rather than filling the disk
    private static readonly object Gate = new();

    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deckard", "logs");

    /// <summary>An error was logged: where, and the exception (for telemetry). Raised on the thread that logged it.</summary>
    public static event Action<string, Exception>? Logged;

    /// <summary>Catches what nothing else did: errors on background threads and unobserved tasks. The UI thread's are App's.</summary>
    public static void CatchUnhandled()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error)
                Write("unhandled", error); // the process ends after this; at least it's logged
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    public static void Write(string where, Exception error)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var file = Path.Combine(Folder, $"deckard-{DateTime.Now:yyyyMMdd}.log");
                if (!File.Exists(file) || new FileInfo(file).Length < MaxFileBytes)
                    File.AppendAllText(file, $"{DateTimeOffset.Now:O} {where} (Deckard {Version}){Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
                foreach (var old in Directory.EnumerateFiles(Folder, "deckard-*.log"))
                    if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-KeepDays))
                        File.Delete(old);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Can't log: nothing more to do about it.
        }
        try
        {
            Logged?.Invoke(where, error);
        }
        catch (Exception e)
        {
            // A failing listener mustn't turn logging into another crash.
            System.Diagnostics.Debug.WriteLine(e);
        }
    }

    /// <summary>
    /// The end of the last <see cref="ReportDays"/> days' logs (at most <paramref name="maxChars"/>), with the Windows user's
    /// folder hidden, for a bug report's diagnostics, which are public. Empty when nothing was logged.
    /// </summary>
    public static string Recent(int maxChars = 8000)
    {
        try
        {
            if (!Directory.Exists(Folder))
                return "";
            var text = new StringBuilder();
            foreach (var file in Directory.EnumerateFiles(Folder, "deckard-*.log").OrderByDescending(f => f)
                         .Where(f => File.GetLastWriteTime(f) > DateTime.Now.AddDays(-ReportDays)))
            {
                text.Insert(0, File.ReadAllText(file));
                if (text.Length >= maxChars)
                    break;
            }
            var recent = text.Length > maxChars ? text.ToString(text.Length - maxChars, maxChars) : text.ToString();
            return HidePersonalPaths(recent);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>Deckard's own frames of <paramref name="error"/>'s stack trace, for telemetry (<see cref="Redaction.OwnFrames"/>).</summary>
    public static string OwnFrames(Exception error) => Redaction.OwnFrames(error.StackTrace);

    /// <summary>The Windows user's folder (and so their user name) replaced by %USERPROFILE%.</summary>
    public static string HidePersonalPaths(string text) =>
        Redaction.HideUserFolder(text, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>How far back a bug report's errors go: older ones are seldom about the bug, and reports are public.</summary>
    public const int ReportDays = 3;

    public static string Version { get; } = typeof(ErrorLog).Assembly.GetName().Version?.ToString(3) ?? "?";
}
