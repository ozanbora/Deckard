using System.Security.Claims;
using Deckard.Core.Trade;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Deckard.Trade.Api;

/// <summary>
/// Bug reports and feature requests from the app. The player's screenshots are covered for personal data on their PC
/// before upload; here they're checked and stripped of metadata (<see cref="ImageSanitizer"/>) and kept in
/// Trade:FeedbackRoot, never in the repository or on GitHub. <see cref="GitHubIssues"/> files each report as an issue
/// that names no player. The server keeps who sent it, for moderation.
/// </summary>
public static class FeedbackEndpoints
{
    public const int MaxTitle = 120, MaxDescription = 5000, MaxDiagnostics = 16 * 1024, MaxScreenshots = 3, MaxScreenshotBytes = 5 * 1024 * 1024;
    public const int PerDay = 5;

    public static void MapFeedbackEndpoints(this WebApplication app)
    {
        app.MapPost("/feedback", async (HttpRequest http, ClaimsPrincipal player, TradeDb db, IConfiguration config, GitHubIssues github) =>
        {
            if (!http.HasFormContentType)
                return Results.BadRequest("Send the report as a form.");
            var me = ListingEndpoints.PlayerId(player);
            if (await db.Users.FindAsync(me) is not { Banned: false })
                return Results.Forbid();
            if (await db.FeedbackMutes.AnyAsync(m => m.UserId == me))
                return Results.Problem("This account can't send feedback.", statusCode: StatusCodes.Status403Forbidden);
            var since = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);
            if (await db.Feedback.CountAsync(f => f.UserId == me && f.Created > since) >= PerDay)
                return Results.Problem($"You can send {PerDay} reports a day; thanks for all of them.", statusCode: StatusCodes.Status429TooManyRequests);

            var form = await http.ReadFormAsync();
            var kind = form["kind"].ToString();
            var title = form["title"].ToString().Trim();
            var description = form["description"].ToString().Trim();
            var diagnostics = form["diagnostics"].ToString().Trim();
            var errors = new List<string>();
            if (kind is not ("bug" or "feature"))
                errors.Add("Say whether it's a bug or a feature request.");
            if (title.Length is 0 or > MaxTitle)
                errors.Add($"The title must be 1-{MaxTitle} characters.");
            if (description.Length is 0 or > MaxDescription)
                errors.Add($"The description must be 1-{MaxDescription} characters.");
            if (diagnostics.Length > MaxDiagnostics)
                errors.Add($"The diagnostics must be at most {MaxDiagnostics / 1024} KB.");
            var files = form.Files.GetFiles("screenshot");
            if (files.Count > MaxScreenshots)
                errors.Add($"Attach at most {MaxScreenshots} screenshots.");
            var images = new List<(byte[] Image, string Extension)>();
            foreach (var file in files.Take(MaxScreenshots))
            {
                if (file.Length is 0 or > MaxScreenshotBytes)
                {
                    errors.Add($"Each screenshot must be at most {MaxScreenshotBytes / 1024 / 1024} MB.");
                    continue;
                }
                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer);
                if (ImageSanitizer.Clean(buffer.ToArray()) is { } clean)
                    images.Add(clean);
                else
                    errors.Add("Screenshots must be PNG or JPEG images.");
            }
            if (errors.Count > 0)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["feedback"] = [.. errors.Distinct()] });

            var row = new FeedbackRow
            {
                UserId = me, Kind = kind, Title = title, Description = description, Diagnostics = diagnostics.Length > 0 ? diagnostics : null,
                Screenshots = images.Count, Created = DateTimeOffset.UtcNow,
            };
            db.Feedback.Add(row);
            await db.SaveChangesAsync();
            if (images.Count > 0)
            {
                var folder = Directory.CreateDirectory(Path.Combine(Root(config), row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                for (var i = 0; i < images.Count; i++)
                    await File.WriteAllBytesAsync(Path.Combine(folder.FullName, $"{i + 1}.{images[i].Extension}"), images[i].Image);
            }
            await github.TryFileAsync(db, row); // when GitHub isn't set up or doesn't answer, the filer tries again later
            return Results.Ok(new FeedbackReceipt(row.Id, row.IssueUrl));
        }).DisableAntiforgery() // a token API: no cookies to forge
          .WithMetadata(new RequestSizeLimitAttribute(MaxScreenshots * MaxScreenshotBytes + 64 * 1024))
          .RequireRateLimiting("player");
    }

    /// <summary>Where screenshots are kept: Trade:FeedbackRoot, else a folder in this user's local app data (never the repo).</summary>
    public static string Root(IConfiguration config) => config["Trade:FeedbackRoot"] is { Length: > 0 } root
        ? root
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deckard.Trade", "feedback");
}
