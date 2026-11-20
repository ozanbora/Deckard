using Deckard.Core.Diagnostics;

namespace Deckard.Core.Tests;

public class RedactionTests
{
    private const string Trace = """
           at System.Text.Json.JsonSerializer.Deserialize(String json)
           at Deckard.App.MainWindow.ReadLibrary() in C:\Users\jane.doe\src\Deckard.App\MainWindow.xaml.cs:line 518
           at Deckard.Core.Builds.BuildPlanLoader.LoadAsync(String link, HttpClient http) in C:\Users\jane.doe\src\BuildPlan.cs:line 90
           at Microsoft.UI.Xaml.Application.Start()
        """;

    [Fact]
    public void KeepsOnlyDeckardFramesWithoutPaths()
    {
        Assert.Equal("at Deckard.App.MainWindow.ReadLibrary()\nat Deckard.Core.Builds.BuildPlanLoader.LoadAsync(String link, HttpClient http)",
            Redaction.OwnFrames(Trace));
    }

    [Fact]
    public void OwnFramesOfNothingIsEmpty() => Assert.Equal("", Redaction.OwnFrames(null));

    [Fact]
    public void OwnFramesAreCapped() => Assert.Equal(20, Redaction.OwnFrames(Trace, maxChars: 20).Length);

    [Fact]
    public void HidesTheUserFolderInAnyCase()
    {
        Assert.Equal(@"failed at %USERPROFILE%\AppData\x.json and %USERPROFILE%\y",
            Redaction.HideUserFolder(@"failed at C:\Users\Jane.Doe\AppData\x.json and c:\users\jane.doe\y", @"C:\Users\jane.doe"));
    }
}
