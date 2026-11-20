using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deckard.Core.Trade;
using Deckard.Trade.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Deckard.Trade.Tests;

public class ImageSanitizerTests
{
    private static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        return chunk; // the CRC is left zero: the sanitizer copies chunks, it doesn't check them
    }

    /// <summary>A PNG with a text chunk holding personal data between the header and the pixels.</summary>
    public static byte[] Png() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        .. Chunk("IHDR", new byte[13]), .. Chunk("tEXt", Encoding.ASCII.GetBytes("Author\0Player#1234")), .. Chunk("IDAT", [1, 2, 3]), .. Chunk("IEND", []),
    ];

    [Fact]
    public void DropsPngTextAndJpegExif()
    {
        var png = ImageSanitizer.Clean(Png())!.Value;
        Assert.Equal("png", png.Extension);
        Assert.DoesNotContain("Player#1234", Encoding.ASCII.GetString(png.Image));
        Assert.Contains("IDAT", Encoding.ASCII.GetString(png.Image));

        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x0F, .. "Exif\0\0GPS:52N"u8, 0xFF, 0xDB, 0x00, 0x04, 0x01, 0x02, 0xFF, 0xDA, 0x00, 0x04, 0x00, 0x00, 0x11, 0x22, 0xFF, 0xD9];
        var clean = ImageSanitizer.Clean(jpeg)!.Value;
        Assert.Equal("jpg", clean.Extension);
        Assert.DoesNotContain("GPS", Encoding.ASCII.GetString(clean.Image));
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x04, 0x01, 0x02, 0xFF, 0xDA, 0x00, 0x04, 0x00, 0x00, 0x11, 0x22, 0xFF, 0xD9], clean.Image);

        Assert.Null(ImageSanitizer.Clean("not an image"u8.ToArray()));
        Assert.Null(ImageSanitizer.Clean(Png()[..^12])); // cut off before IEND
    }

    [Fact]
    public void KeepsPlayersWordsInert()
    {
        Assert.Equal("````text\nuse ``` here\n````", GitHubIssues.Fenced("use ``` here"));
        Assert.Equal("Hi @​ozanbora see #​1", GitHubIssues.Quiet("Hi @ozanbora see #1"));
    }
}

[Collection("TradeDb")]
public class FeedbackTests
{
    private static MultipartFormDataContent Report(string title, string kind = "bug", int screenshots = 1, byte[]? image = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(kind), "kind" },
            { new StringContent(title), "title" },
            { new StringContent("Hover a ring @ozanbora ``` [click](https://evil.example)"), "description" },
            { new StringContent("Deckard 1.4.0, Windows 11, 2560x1440"), "diagnostics" },
        };
        for (var i = 0; i < screenshots; i++)
            form.Add(new ByteArrayContent(image ?? ImageSanitizerTests.Png()), "screenshot", $"shot{i}.png");
        return form;
    }

    [DatabaseFact]
    public async Task FilesAnIssueThatNamesNoPlayer()
    {
        using var api = new TradeApi();
        var player = await api.SignedIn("Player#1234");

        var sent = await player.PostAsync("/feedback", Report("Tooltip @ozanbora misread"));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var receipt = (await sent.Content.ReadFromJsonAsync<FeedbackReceipt>())!;
        Assert.Equal(FakeGitHub.IssueUrl, receipt.IssueUrl);

        var issue = Assert.Single(FakeGitHub.Created);
        Assert.Equal("Bug: Tooltip @​ozanbora misread", issue.GetProperty("title").GetString());
        Assert.Equal(["bug", "from-app"], issue.GetProperty("labels").EnumerateArray().Select(l => l.GetString()));
        var body = issue.GetProperty("body").GetString()!;
        Assert.Contains($"Report #{receipt.ReportId}", body);
        Assert.Contains("````text\nHover a ring", body);  // fenced past the player's own ```
        Assert.DoesNotContain("Player#1234", body);
        Assert.Contains("1 screenshot kept on the trade server", body);

        var stored = Path.Combine(api.FeedbackRoot, receipt.ReportId.ToString(), "1.png");
        Assert.DoesNotContain("Player#1234", Encoding.ASCII.GetString(await File.ReadAllBytesAsync(stored))); // metadata gone
        using (var scope = api.Services.CreateScope())
        {
            var output = new StringWriter();
            await Moderation.RunCommandAsync(scope.ServiceProvider.GetRequiredService<TradeDb>(), ["--feedback", receipt.ReportId.ToString()], output, api.FeedbackRoot);
            Assert.Contains("deckard-feedback-", output.ToString());
        }
    }

    [DatabaseFact]
    public async Task ChecksReportsAndLimitsThem()
    {
        using var api = new TradeApi();
        var player = await api.SignedIn("Player#1234");

        Assert.Equal(HttpStatusCode.BadRequest, (await player.PostAsync("/feedback", Report("x", kind: "rant"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await player.PostAsync("/feedback", Report(""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await player.PostAsync("/feedback", Report("x", screenshots: 4))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await player.PostAsync("/feedback", Report("x", image: "<script>"u8.ToArray()))).StatusCode);
        for (var i = 0; i < FeedbackEndpoints.PerDay; i++)
            Assert.Equal(HttpStatusCode.OK, (await player.PostAsync("/feedback", Report($"report {i}", kind: "feature", screenshots: 0))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await player.PostAsync("/feedback", Report("one more", screenshots: 0))).StatusCode);

        var other = await api.SignedIn("Other#5678");
        using (var scope = api.Services.CreateScope())
            await Moderation.RunCommandAsync(scope.ServiceProvider.GetRequiredService<TradeDb>(), ["--nofeedback", "Other#5678"], TextWriter.Null);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync("/feedback", Report("hi", screenshots: 0))).StatusCode);
    }

    [DatabaseFact]
    public async Task FilesLaterWhenGitHubIsDownAndDropsScreenshotsAfterTheIssueCloses()
    {
        using var api = new TradeApi();
        var player = await api.SignedIn("Player#1234");
        var receipt = (await (await player.PostAsync("/feedback", Report(FakeGitHub.Down))).Content.ReadFromJsonAsync<FeedbackReceipt>())!;
        Assert.Null(receipt.IssueUrl); // kept, waiting

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeDb>();
        var github = scope.ServiceProvider.GetRequiredService<GitHubIssues>();
        await db.Feedback.ExecuteUpdateAsync(f => f.SetProperty(x => x.Title, "GitHub is back"));
        await github.RunAsync(db, api.FeedbackRoot, DateTimeOffset.UtcNow);
        var report = await db.Feedback.AsNoTracking().SingleAsync();
        Assert.Equal(FakeGitHub.IssueNumber, report.IssueNumber);
        Assert.NotNull(report.IssueClosedAt);                        // the fake issue is closed
        Assert.True(Directory.Exists(Path.Combine(api.FeedbackRoot, report.Id.ToString())));

        await github.RunAsync(db, api.FeedbackRoot, report.IssueClosedAt!.Value + GitHubIssues.KeepScreenshots + TimeSpan.FromDays(1));
        Assert.False(Directory.Exists(Path.Combine(api.FeedbackRoot, report.Id.ToString())));
        Assert.Equal(0, (await db.Feedback.AsNoTracking().SingleAsync()).Screenshots);
    }
}

/// <summary>GitHub's issue endpoints: records created issues; a title of <see cref="Down"/> fails as if GitHub were down.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public const string Api = "https://github.test/", Down = "GitHub is down", IssueUrl = "https://github.test/ozanbora/deckard/issues/7";
    public const int IssueNumber = 7;
    public static readonly List<JsonElement> Created = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization?.Parameter != "github-token")
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path == "/repos/ozanbora/deckard/issues")
        {
            var issue = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            if (issue.GetProperty("title").GetString()!.EndsWith(Down, StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            lock (Created)
                Created.Add(issue);
            return Json($$"""{ "number": {{IssueNumber}}, "html_url": "{{IssueUrl}}" }""");
        }
        if (request.Method == HttpMethod.Get && path == $"/repos/ozanbora/deckard/issues/{IssueNumber}")
            return Json("""{ "number": 7, "state": "closed", "closed_at": "2026-10-01T12:00:00Z" }""");
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
