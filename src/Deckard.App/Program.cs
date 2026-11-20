using Velopack;

namespace Deckard.App;

/// <summary>
/// Deckard's entry point: Velopack's install, update and uninstall hooks run first (they exit at once on a normal
/// start), then WinUI starts as its generated Main would (DISABLE_XAML_GENERATED_MAIN in the project).
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main()
    {
        ErrorLog.CatchUnhandled();
        VelopackApp.Build().Run();
        XamlGeneratedProgram.XamlGeneratedMain();
    }
}
