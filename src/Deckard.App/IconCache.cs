using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Deckard.App;

/// <summary>
/// Item, skill and paragon icons from the Deckard server, downloaded once into %LOCALAPPDATA%\Deckard\icons.
/// Icons are game art; they're cached for this app only, never committed.
/// </summary>
internal static class IconCache
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deckard", "icons");
    private static readonly SemaphoreSlim Downloads = new(6); // a tree has ~200 icons; don't open them all at once
    private static readonly Dictionary<long, Task<string?>> Files = [];

    /// <summary>The icon as an image source, or null if it can't be downloaded. Call on the UI thread.</summary>
    public static async Task<ImageSource?> GetAsync(long id) =>
        await GetFileAsync(id) is { } path ? new BitmapImage(new Uri(path)) : null;

    /// <summary>The icon's cached file, or null if it can't be downloaded. Any thread.</summary>
    public static Task<string?> GetFileAsync(long id)
    {
        if (id == 0)
            return Task.FromResult<string?>(null);
        lock (Files)
        {
            if (!Files.TryGetValue(id, out var file))
                Files[id] = file = DownloadAsync(id);
            return file;
        }
    }

    private static async Task<string?> DownloadAsync(long id)
    {
        var path = Path.Combine(Folder, $"{id}.webp");
        if (File.Exists(path))
            return path;
        await Downloads.WaitAsync();
        try
        {
            var bytes = await TradeClient.Shared.IconAsync(id);
            Directory.CreateDirectory(Folder);
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch (Exception e) when (e is TradeException or HttpRequestException or TaskCanceledException or IOException)
        {
            return null; // the node keeps its frame without an icon
        }
        finally
        {
            Downloads.Release();
        }
    }
}
