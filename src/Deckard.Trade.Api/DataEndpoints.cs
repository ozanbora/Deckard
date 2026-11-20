using System.Security.Claims;
using Deckard.Core.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Deckard.Trade.Api;

/// <summary>
/// The files the app downloads: the season's affix-templates.json and the icons it refers to, both under the data root,
/// and app updates. Signed-in players only, like every endpoint, so the app gets its game data only from here. Files
/// carry an ETag: the app downloads the data again only when it changed.
/// </summary>
public static class DataEndpoints
{
    public static void MapDataEndpoints(this WebApplication app)
    {
        // The file of the season the server checks listings against, so app and server always agree.
        app.MapGet("/data/d4/affix-templates.json", (AffixCatalog catalog, IConfiguration config) =>
            File(DataRoot(config) is { } root ? Path.Combine(root, $"season-{catalog.Season}", AffixCatalog.FileName) : null, "application/json"))
            .RequireRateLimiting("data");

        app.MapGet("/icons/{id:long}.webp", (long id, IConfiguration config) =>
            File(DataRoot(config) is { } root ? Path.Combine(root, "icons", $"{id}.webp") : null, "image/webp"))
            .RequireRateLimiting("data");

        // App updates: the release files tools/installer/pack.ps1 writes (releases.win.json and the packages), copied to
        // Trade:UpdatesRoot. Only files inside that folder are served, and never its early/ folder: that's below.
        app.MapGet("/updates/{**file}", (string file, IConfiguration config) =>
            File(config["Trade:UpdatesRoot"] is { Length: > 0 } root && Inside(root, file) is { } path && Inside(Path.Combine(root, EarlyFolder), path) is null ? path : null,
                ContentType(file)))
            .RequireRateLimiting("data");

        // Early-access builds (pack.ps1 -Channel early, copied to early/ under Trade:UpdatesRoot): only for active patrons
        // whose tier gives early access. Anyone else gets 404, as for a file that isn't there.
        app.MapGet("/updates/early/{**file}", async (string file, ClaimsPrincipal player, IConfiguration config, TradeDb db) =>
        {
            var me = ListingEndpoints.PlayerId(player);
            if (!await db.Patrons.AnyAsync(p => p.UserId == me && p.Patron && p.EarlyAccess))
                return Results.NotFound();
            return File(config["Trade:UpdatesRoot"] is { Length: > 0 } root && Inside(Path.Combine(root, EarlyFolder), file) is { } path ? path : null, ContentType(file));
        }).RequireRateLimiting("data");
    }

    private const string EarlyFolder = "early";

    private static string ContentType(string file) =>
        file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json" : "application/octet-stream";

    /// <summary>The file's full path when it lies inside <paramref name="root"/>; null for "..", rooted or other escaping paths.</summary>
    private static string? Inside(string root, string file)
    {
        var folder = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(folder, file));
        // Case matters on Linux hosts: there /srv/Updates is another folder than /srv/updates.
        return path.StartsWith(folder, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? path : null;
    }

    public static string? DataRoot(IConfiguration config) => config["Trade:DataRoot"] ?? AffixCatalog.FindDataRoot();

    private static IResult File(string? path, string contentType)
    {
        if (path is null || !System.IO.File.Exists(path))
            return Results.NotFound();
        var modified = System.IO.File.GetLastWriteTimeUtc(path);
        return TypedResults.PhysicalFile(path, contentType, lastModified: modified,
            entityTag: new EntityTagHeaderValue($"\"{modified.Ticks:x}\""));
    }
}
