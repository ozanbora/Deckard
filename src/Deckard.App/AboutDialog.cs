using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Deckard.App;

/// <summary>About and credits: what Deckard is, the fonts it bundles (with their licenses) and where its data comes from.</summary>
internal static class AboutDialog
{
    private sealed record FontCredit(string Name, string Use, string By, string Site, string LicenseFile);

    private static readonly FontCredit[] Fonts =
    [
        new("PT Serif", "all text, as the typeface of Diablo IV's tooltips", "A. Korolkova, O. Umpeleva, V. Yefimov, ParaType", "https://www.paratype.com/fonts/pt/pt-serif", "PTSerif-OFL.txt"),
    ];

    public static async Task ShowAsync(XamlRoot root)
    {
        var body = new StackPanel { Spacing = 14, MaxWidth = 460 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Deckard reads Diablo IV item tooltips from the screen and compares them with your build. It never touches "
                 + "the game: no memory reading, no hooks, no input.",
        });

        // The AGPL's notices: version, copyright, license, no warranty, and where the source is (the trade server's too).
        var license = new TextBlock { TextWrapping = TextWrapping.Wrap };
        license.Inlines.Add(new Run { Text = $"Version {ErrorLog.Version}. Copyright © 2026 Synica. Deckard is free software under the " });
        license.Inlines.Add(Link("GNU Affero General Public License v3.0", "https://www.gnu.org/licenses/agpl-3.0.html"));
        license.Inlines.Add(new Run { Text = ", without any warranty. " });
        license.Inlines.Add(Link("Source code", SourceUrl));
        license.Inlines.Add(new Run { Text = " of the app and its trade server." });
        body.Children.Add(license);

        body.Children.Add(Heading("Fonts"));
        foreach (var font in Fonts)
        {
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap };
            line.Inlines.Add(new Run { Text = font.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            line.Inlines.Add(new Run { Text = $" for {font.Use}. By {font.By}. SIL Open Font License 1.1. " });
            line.Inlines.Add(Link("Website", font.Site));
            line.Inlines.Add(new Run { Text = " · " });
            line.Inlines.Add(Link("License", new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", font.LicenseFile)).AbsoluteUri));
            body.Children.Add(line);
        }

        body.Children.Add(Heading("Data"));
        var data = new TextBlock { TextWrapping = TextWrapping.Wrap };
        data.Inlines.Add(new Run { Text = "Item data and icons come from " });
        data.Inlines.Add(Link("maxroll.gg", "https://maxroll.gg/d4"));
        data.Inlines.Add(new Run { Text = ". Build plans are imported from " });
        data.Inlines.Add(Link("maxroll.gg", "https://maxroll.gg/d4"));
        data.Inlines.Add(new Run { Text = ", " });
        data.Inlines.Add(Link("d4builds.gg", "https://d4builds.gg"));
        data.Inlines.Add(new Run { Text = " and " });
        data.Inlines.Add(Link("Icy Veins", "https://www.icy-veins.com/d4/"));
        data.Inlines.Add(new Run { Text = ". Diablo is a trademark of Blizzard Entertainment, Inc. Deckard is not affiliated with Blizzard, maxroll, d4builds or Icy Veins." });
        body.Children.Add(data);

        // Patrons who chose to be named in Settings > Patreon; the server lists only active ones.
        body.Children.Add(Heading("Supporters"));
        var supporters = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Loading…", Opacity = 0.7 };
        body.Children.Add(supporters);
        _ = ShowSupportersAsync(supporters);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = new TextBlock { Text = "ABOUT DECKARD", Style = (Style)Application.Current.Resources["DisplayTextStyle"], FontSize = 20 },
            Content = new ScrollViewer { Content = body },
            CloseButtonText = "Close",
        };
        await dialog.ShowAsync();
    }

    private static async Task ShowSupportersAsync(TextBlock text)
    {
        try
        {
            var names = TradeClient.Shared.SignedIn ? await TradeClient.Shared.SupportersAsync() : [];
            text.Text = names.Count > 0 ? "Thank you, " + string.Join(", ", names) + "."
                : "Patrons of Deckard's Patreon page can have their name here (Settings > Patreon).";
            text.Opacity = names.Count > 0 ? 1 : 0.7;
        }
        catch (Exception e) when (e is TradeException or HttpRequestException or TaskCanceledException)
        {
            text.Text = "Can't reach the Deckard server to list them.";
        }
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text.ToUpperInvariant(), Style = (Style)Application.Current.Resources["DisplayTextStyle"], FontSize = 14
    };

    public const string SourceUrl = "https://github.com/ozanbora/Deckard";

    private static Hyperlink Link(string text, string url)
    {
        var link = new Hyperlink { NavigateUri = new Uri(url) };
        link.Inlines.Add(new Run { Text = text });
        return link;
    }
}
