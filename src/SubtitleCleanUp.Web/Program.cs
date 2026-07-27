using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SubtitleCleanUp.Core.Abstractions;
using SubtitleCleanUp.Core.Configuration;
using SubtitleCleanUp.Core.Models;
using SubtitleCleanUp.Core.Services;
using SubtitleCleanUp.Web.Components;
using SubtitleCleanUp.Web.Data;
using SubtitleCleanUp.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.AddFilter("Microsoft.AspNetCore.Components.Server.Circuits", LogLevel.Debug);
builder.Logging.AddFilter("Microsoft.AspNetCore.SignalR", LogLevel.Debug);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Connections", LogLevel.Debug);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
var quarantineRoot = builder.Configuration[$"{SubtitleCleanupOptions.SectionName}:QuarantineRoot"];
if (string.IsNullOrWhiteSpace(quarantineRoot))
{
    quarantineRoot = "/data/quarantine";
}
var dataRoot = Directory.GetParent(Path.GetFullPath(quarantineRoot))?.FullName
    ?? Path.GetFullPath(quarantineRoot);
var keyDirectory = Directory.CreateDirectory(Path.Combine(dataRoot, "keys"));
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(keyDirectory)
    .SetApplicationName("SubtitleCleanUp");
builder.Services.AddOptions<SubtitleCleanupOptions>()
    .Bind(builder.Configuration.GetSection(SubtitleCleanupOptions.SectionName))
    .Validate(options => options.Roots.Count > 0, "At least one media root must be configured.")
    .Validate(options => options.Roots.All(x =>
        !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Path)),
        "Every media root requires a name and path.")
    .Validate(options => options.Roots.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
                         options.Roots.Count,
        "Media root names must be unique.")
    .Validate(options =>
    {
        try
        {
            _ = FiveFieldCronSchedule.Parse(options.ScanCron);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }, "ScanCron must be a valid five-field cron expression.")
    .Validate(options =>
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
    }, "TimeZone must be a valid system time-zone identifier.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.QuarantineRoot),
        "QuarantineRoot is required.")
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("SubtitleCleanup")
    ?? "Data Source=subtitlecleanup.db";
builder.Services.AddDbContextFactory<SubtitleCleanupDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddSingleton<IsoLanguageCatalog>();
builder.Services.AddSingleton<ISubtitleFilenameParser, SubtitleFilenameParser>();
builder.Services.AddSingleton<ISubtitleFileSystem, PhysicalSubtitleFileSystem>();
builder.Services.AddSingleton<ISubtitleScanner, SubtitleScanner>();
builder.Services.AddSingleton<IChangePlanner, ChangePlanner>();
builder.Services.AddSingleton<ISystemClock, SystemClock>();
builder.Services.AddSingleton<OperationGate>();
builder.Services.AddSingleton<ScanCoordinator>();
builder.Services.AddSingleton<ChangeExecutionService>();
builder.Services.AddSingleton<SubtitlePreviewService>();
builder.Services.AddSingleton<ScanScheduler>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ScanScheduler>());

var app = builder.Build();
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseStaticFiles();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (
    IDbContextFactory<SubtitleCleanupDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    return await db.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});
app.MapGet("/api/queue", async (
    IDbContextFactory<SubtitleCleanupDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var count = await db.ChangeProposals.CountAsync(
        proposal => proposal.Status == ProposalStatus.Pending,
        cancellationToken);
    return Results.Ok(new { count });
});

await using (var db = await app.Services
                 .GetRequiredService<IDbContextFactory<SubtitleCleanupDbContext>>()
                 .CreateDbContextAsync())
{
    await db.Database.MigrateAsync();
}

var blazorBootstrapScriptPath = Path.Combine(app.Environment.WebRootPath ?? string.Empty, "_framework", "blazor.web.js");
startupLogger.LogInformation(
    "Web root resolved to {WebRootPath}. Blazor bootstrap script present: {BootstrapScriptPresent} at {BootstrapScriptPath}.",
    app.Environment.WebRootPath,
    File.Exists(blazorBootstrapScriptPath),
    blazorBootstrapScriptPath);

app.Run();

public partial class Program;
