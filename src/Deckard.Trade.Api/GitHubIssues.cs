using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Deckard.Trade.Api;

/// <summary>
/// Files feedback reports as GitHub issues in Trade:GitHub:Repository ("owner/name") with Trade:GitHub:Token, a token
/// allowed only issues on that repository. The issue names no player: a report number, the player's own words inside a
/// code block (so no mention, link or markdown in them comes alive), the diagnostics, and how many screenshots the server
/// keeps; screenshots never go to GitHub. The project board picks the issues up with its own "Auto-add to project"
/// workflow (label from-app). Reports wait while GitHub isn't configured or doesn't answer; the background loop files
/// them later, and deletes screenshots 90 days after their issue closes (or a year after the report, if never filed).
/// </summary>
public sealed class GitHubIssues(IHttpClientFactory clients, IConfiguration config, ILogger<GitHubIssues> log)
{
    public static readonly TimeSpan KeepScreenshots = TimeSpan.FromDays(90), KeepUnfiled = TimeSpan.FromDays(365);

    private sealed record Settings(string Repository, string Token, Uri Api);

    private Settings? Configured() =>
        config["Trade:GitHub:Token"] is { Length: > 0 } token && config["Trade:GitHub:Repository"] is { Length: > 0 } repository
            ? new Settings(repository, token, new Uri(config["Trade:GitHub:ApiUrl"] ?? "https://api.github.com"))
            : null;

    /// <summary>Files the report if GitHub is configured; otherwise, or on failure, it stays pending.</summary>
    public async Task TryFileAsync(TradeDb db, FeedbackRow report)
    {
        if (report.IssueNumber is not null || Configured() is not { } github)
            return;
        try
        {
            using var response = await Send(github, HttpMethod.Post, $"repos/{github.Repository}/issues", new
            {
                title = $"{(report.Kind == "bug" ? "Bug" : "Feature request")}: {Quiet(report.Title)}",
                body = Body(report),
                labels = new[] { report.Kind == "bug" ? "bug" : "enhancement", "from-app" },
            });
            response.EnsureSuccessStatusCode();
            using var issue = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            report.IssueNumber = issue.RootElement.GetProperty("number").GetInt32();
            report.IssueUrl = issue.RootElement.GetProperty("html_url").GetString();
            await db.SaveChangesAsync();
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
            log.LogWarning(e, "Couldn't file feedback report {Report} on GitHub; it stays pending.", report.Id);
        }
    }

    /// <summary>Files pending reports, notes which issues closed, and deletes screenshots that were kept long enough.</summary>
    public async Task RunAsync(TradeDb db, string screenshotRoot, DateTimeOffset now)
    {
        foreach (var pending in await db.Feedback.Where(f => f.IssueNumber == null).OrderBy(f => f.Id).Take(20).ToListAsync())
            await TryFileAsync(db, pending);

        if (Configured() is { } github)
        {
            foreach (var open in await db.Feedback.Where(f => f.IssueNumber != null && f.IssueClosedAt == null && f.Screenshots > 0).Take(50).ToListAsync())
            {
                try
                {
                    using var response = await Send(github, HttpMethod.Get, $"repos/{github.Repository}/issues/{open.IssueNumber}", null);
                    response.EnsureSuccessStatusCode();
                    using var issue = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (issue.RootElement.TryGetProperty("closed_at", out var closed) && closed.ValueKind == JsonValueKind.String)
                        open.IssueClosedAt = closed.GetDateTimeOffset();
                }
                catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
                {
                    log.LogWarning(e, "Couldn't read issue {Issue}.", open.IssueNumber);
                }
            }
            await db.SaveChangesAsync();
        }

        var closedBefore = now - KeepScreenshots;
        var unfiledBefore = now - KeepUnfiled;
        foreach (var done in await db.Feedback.Where(f => f.Screenshots > 0
                     && ((f.IssueClosedAt != null && f.IssueClosedAt < closedBefore) || (f.IssueNumber == null && f.Created < unfiledBefore))).ToListAsync())
        {
            var folder = Path.Combine(screenshotRoot, done.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            done.Screenshots = 0;
        }
        await db.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> Send(Settings github, HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(github.Api, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", github.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Deckard-Trade", "1"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await clients.CreateClient("github").SendAsync(request);
    }

    /// <summary>The issue text: the report number, then the player's words and the diagnostics, each in its own code block.</summary>
    internal static string Body(FeedbackRow report)
    {
        var text = new StringBuilder();
        text.AppendLine($"Report #{report.Id} from the Deckard app ({(report.Kind == "bug" ? "bug report" : "feature request")}).");
        text.AppendLine().AppendLine("**Description**").AppendLine(Fenced(report.Description));
        if (report.Diagnostics is { } diagnostics)
            text.AppendLine().AppendLine("<details><summary>Diagnostics</summary>").AppendLine().AppendLine(Fenced(diagnostics)).AppendLine("</details>");
        if (report.Screenshots > 0)
            text.AppendLine().AppendLine($"{report.Screenshots} screenshot{(report.Screenshots == 1 ? "" : "s")} kept on the trade server: "
                + $"`dotnet run -- --feedback {report.Id}` copies them to a folder.");
        return text.ToString();
    }

    /// <summary>Text in a code block fenced with more backticks than any run in it, so nothing in it can close the block.</summary>
    internal static string Fenced(string text)
    {
        var longest = 0;
        for (int i = 0, run = 0; i < text.Length; i++)
            longest = Math.Max(longest, run = text[i] == '`' ? run + 1 : 0);
        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}text\n{text}\n{fence}";
    }

    /// <summary>A title can't mention anyone or link an issue: a zero-width space after each @ and #.</summary>
    internal static string Quiet(string title) => title.Replace("@", "@​").Replace("#", "#​");
}

/// <summary>Runs <see cref="GitHubIssues.RunAsync"/> every half hour.</summary>
public sealed class FeedbackFiler(IServiceScopeFactory scopes, GitHubIssues github, IConfiguration config, ILogger<FeedbackFiler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TradeDb>();
                // One instance at a time, so a report is filed as one issue.
                await SingleInstance.TryRunAsync(db, SingleInstance.Feedback, () => github.RunAsync(db, FeedbackEndpoints.Root(config), DateTimeOffset.UtcNow), stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Feedback filing failed; it runs again in half an hour.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
