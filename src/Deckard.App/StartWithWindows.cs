using Microsoft.Win32;

namespace Deckard.App;

/// <summary>
/// Whether Deckard starts when the player signs in to Windows: a value under the user's Run key, so it's the Windows
/// user's choice rather than one account's. Started that way, Deckard opens in the tray (--tray).
/// </summary>
internal static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Deckard";
    public const string TrayArgument = "--tray";

    public static bool IsOn
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string command && command.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                key.SetValue(Name, $"\"{Environment.ProcessPath}\" {TrayArgument}");
            else
                key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}
