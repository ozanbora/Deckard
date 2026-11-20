using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Deckard.Core.Items;
using Deckard.Trade.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Secrets come from user secrets in development and from the host's settings in production, never from the repo.
var signingKey = builder.Configuration["Trade:SigningKey"] is { Length: >= 32 } key
    ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key))
    : throw new InvalidOperationException("Trade:SigningKey (32+ characters) is not configured.");
builder.Services.AddSingleton(new TokenIssuer(signingKey));

var connectionString = builder.Configuration.GetConnectionString("Trade") ?? throw new InvalidOperationException("ConnectionStrings:Trade is not configured.");
// Development maps the stand-in sign-in, so it only ever runs on a database on this machine, never the live one.
if (builder.Environment.IsDevelopment() && !DevelopmentSchema.IsLocal(connectionString))
    throw new InvalidOperationException("Development runs only against a database on this machine (localhost).");
builder.Services.AddDbContext<TradeDb>(options => options.UseNpgsql(connectionString));

// No request needs more; feedback (screenshots) and telemetry set their own limits.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 256 * 1024);

// The season's item data checks every listing. Tests replace the catalog with a small fixture.
builder.Services.AddSingleton(_ => AffixCatalog.LoadSeason(DataEndpoints.DataRoot(builder.Configuration)
    ?? throw new InvalidOperationException("No season data: set Trade:DataRoot, or put the season data in data/d4.")));
builder.Services.AddSingleton<ListingRules>();
builder.Services.AddHostedService<RetentionCleanup>(); // deletes trades that ended over a year ago (privacy policy)
builder.Services.AddMemoryCache(); // the credits list (sign-ins in progress are in the database: PendingSignIns)
builder.Services.AddHttpClient("battlenet", http => http.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient("patreon", http => http.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient("github", http => http.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<GitHubIssues>();
builder.Services.AddHostedService<FeedbackFiler>(); // files feedback as GitHub issues, deletes old screenshots

// Every endpoint but the sign-in steps needs a token, and tokens are only issued to signed-in players: the API has no anonymous
// surface. No CORS policy is registered, so web pages on other sites can't call it from a browser.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = TokenIssuer.ValidationParameters(signingKey);
    options.Events = new JwtBearerEvents { OnTokenValidated = Sessions.CheckAsync }; // a token works only while its session does
});
builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// Per-player limits: a script with a stolen or real account can't flood the market or download it.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("player", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("listing", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromHours(1) }));
    // The app fetches a build's icons in one burst (a skill tree has ~200) the first time it shows them.
    options.AddPolicy("data", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 1000, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// The schema comes from EF migrations (Migrations/), applied at start in every environment. A schema change:
// "dotnet tool run dotnet-ef migrations add <Name> --project src/Deckard.Trade.Api".
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TradeDb>();
    if (app.Environment.IsDevelopment())
    {
        if (args.Contains("--reset"))
            await db.Database.EnsureDeletedAsync(); // "--reset": drop the development database
        await DevelopmentSchema.AdoptAsync(db);     // one made before migrations
    }
    // The lock needs the database: a new one is made first (as the migrations would), by whichever instance gets there.
    var creator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>(db);
    if (!await creator.ExistsAsync())
    {
        try
        {
            await creator.CreateAsync();
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == Npgsql.PostgresErrorCodes.DuplicateDatabase)
        {
            // another instance made it a moment ago
        }
    }
    // Several instances starting at once migrate one after the other (the others then find nothing to do).
    await SingleInstance.RunAsync(db, SingleInstance.Startup, () => db.Database.MigrateAsync());
}

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TradeDb>();
    // "dotnet run -- --reset --seed 500": fill the development market with dummy listings, then exit.
    if (Array.IndexOf(args, "--seed") is var at and >= 0)
    {
        var count = at + 1 < args.Length && int.TryParse(args[at + 1], out var n) ? n : 500;
        var added = await DummyData.SeedAsync(db, scope.ServiceProvider.GetRequiredService<AffixCatalog>(), scope.ServiceProvider.GetRequiredService<ListingRules>(), count);
        Console.WriteLine($"Added {added} dummy listings.");
        return;
    }
    // "dotnet run -- --demo <BattleTag>": a market around that signed-in player with something of every kind, then exit.
    if (Array.IndexOf(args, "--demo") is var demo and >= 0 && demo + 1 < args.Length)
    {
        Console.WriteLine(await DemoData.SeedAsync(db, scope.ServiceProvider.GetRequiredService<AffixCatalog>(), scope.ServiceProvider.GetRequiredService<ListingRules>(), args[demo + 1]));
        return;
    }
}

// Started on a new season's data: the old seasons' listings and buy orders move to the eternal realm, as the items do in game.
using (var scope = app.Services.CreateScope())
{
    // Older season data still works, but players miss what this version adds (rune text, set bonuses...).
    if (scope.ServiceProvider.GetRequiredService<AffixCatalog>() is { IsOutdated: true } data)
        app.Logger.LogWarning("The game data is revision {Revision}; this server expects {Expected}. Produce the season data again into the data root and restart.",
            data.DataRevision, AffixCatalog.ExpectedDataRevision);
    var rolloverDb = scope.ServiceProvider.GetRequiredService<TradeDb>();
    var moved = 0; // one instance at a time, like the migrations: the next finds nothing left to move
    await SingleInstance.RunAsync(rolloverDb, SingleInstance.Startup,
        async () => moved = await SeasonRollover.RunAsync(rolloverDb, scope.ServiceProvider.GetRequiredService<ListingRules>().Season));
    if (moved > 0)
        app.Logger.LogInformation("Season rollover: moved {Count} listings and buy orders to the eternal realm.", moved);
    // "--reports", "--unflag <BattleTag>", "--ban <BattleTag>", "--uncredit <BattleTag>", "--nofeedback <BattleTag>",
    // "--feedback <report>": moderation, then exit.
    if (await Moderation.RunCommandAsync(scope.ServiceProvider.GetRequiredService<TradeDb>(), args, Console.Out, FeedbackEndpoints.Root(app.Configuration)))
        return;
}

app.MapAuthEndpoints();
app.MapBattleNetLogin();
app.MapPatreonLink();
app.MapListingEndpoints();
app.MapOfferEndpoints();
app.MapPresenceEndpoints();
app.MapWantedEndpoints();
app.MapBlockEndpoints();
app.MapTelemetryEndpoints();
app.MapFeedbackEndpoints();
app.MapAccountEndpoints();
app.MapDataEndpoints();

app.Run();

public partial class Program;
