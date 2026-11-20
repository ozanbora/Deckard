using Deckard.Core.Items;

namespace Deckard.App;

/// <summary>
/// The season's item data, downloaded from the Deckard server into %LOCALAPPDATA%\Deckard\data and fetched again only
/// when the server's copy changes. Offline, the last download is used; without one, loading fails with the reason.
/// </summary>
internal static class GameData
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deckard", "data");
    private static readonly string DataFile = Path.Combine(Folder, AffixCatalog.FileName);
    private static readonly string TagFile = DataFile + ".etag";

    /// <summary>Why the last load used the saved copy instead of the server's (it may be older), or null when it didn't.</summary>
    public static string? Problem { get; private set; }

    /// <exception cref="FileNotFoundException">No data could be downloaded and none was saved before; the message says why.</exception>
    public static async Task<AffixCatalog> LoadAsync(TradeClient client)
    {
        string? problem = null;
        Problem = null;
        try
        {
            var etag = File.Exists(DataFile) && File.Exists(TagFile) ? File.ReadAllText(TagFile) : null;
            if (await client.GameDataAsync(etag) is { } fresh)
            {
                AffixCatalog.Load(new MemoryStream(fresh.Data)); // never replace a working copy with one that doesn't load
                Directory.CreateDirectory(Folder);
                await File.WriteAllBytesAsync(DataFile + ".part", fresh.Data);
                File.Move(DataFile + ".part", DataFile, overwrite: true);
                await File.WriteAllTextAsync(TagFile, fresh.ETag ?? "");
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            problem = "the Deckard server can't be reached.";
        }
        catch (Exception e) when (e is TradeException or IOException or InvalidDataException or NotSupportedException or System.Text.Json.JsonException)
        {
            problem = e.Message; // e.g. data in a newer schema than this version of Deckard reads
        }

        if (File.Exists(DataFile))
        {
            Problem = problem;
            return AffixCatalog.Load(DataFile);
        }
        throw new FileNotFoundException($"Couldn't download the game data: {problem ?? "the server sent none."}", DataFile);
    }
}
