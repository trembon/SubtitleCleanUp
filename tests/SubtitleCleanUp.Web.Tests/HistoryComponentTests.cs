using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using SubtitleCleanUp.Core.Abstractions;
using SubtitleCleanUp.Core.Configuration;
using SubtitleCleanUp.Core.Models;
using SubtitleCleanUp.Core.Services;
using SubtitleCleanUp.Web.Components.Layout;
using SubtitleCleanUp.Web.Components.Pages;
using SubtitleCleanUp.Web.Data;
using SubtitleCleanUp.Web.Services;

namespace SubtitleCleanUp.Web.Tests;

public sealed class HistoryComponentTests
{
    [Fact]
    public async Task File_operations_are_paged()
    {
        using var factory = new TestDbFactory();
        await SeedOperationsAsync(factory, 26);
        using var context = CreateContext(factory);

        var component = context.Render<FileOperations>();

        component.FindAll("tbody tr").Count.ShouldBe(25);
        component.Markup.ShouldContain("Showing 1-25 of 26");
        component.Find("a[href=\"/history/operations?page=2\"]").ShouldNotBeNull();
    }

    [Fact]
    public async Task Recent_scans_are_paged()
    {
        using var factory = new TestDbFactory();
        await SeedScansAsync(factory, 26);
        using var context = CreateContext(factory);

        var component = context.Render<RecentScans>();

        component.FindAll(".timeline-item").Count.ShouldBe(25);
        component.Markup.ShouldContain("Showing 1-25 of 26");
        component.Find("a[href=\"/history/scans?page=2\"]").ShouldNotBeNull();
    }

    [Fact]
    public async Task Quarantine_shows_recovery_actions_and_pagination()
    {
        using var factory = new TestDbFactory();
        await SeedOperationsAsync(factory, 26, OperationType.Quarantine, OperationStatus.Applied);
        using var context = CreateContext(factory);

        var component = context.Render<Quarantine>();

        component.FindAll(".operation-row").Count.ShouldBe(25);
        component.FindAll(".operation-row button").Count.ShouldBe(50);
        component.Find("a[href=\"/history/quarantine?page=2\"]").ShouldNotBeNull();
    }

    [Fact]
    public void Empty_history_pages_show_their_empty_states()
    {
        using var factory = new TestDbFactory();
        using var context = CreateContext(factory);

        context.Render<Quarantine>().Markup.ShouldContain("Quarantine is empty");
        context.Render<RecentScans>().Markup.ShouldContain("No scans yet");
        context.Render<FileOperations>().Markup.ShouldContain("No file operations yet");
    }

    [Fact]
    public void Sidebar_safety_note_is_removed()
    {
        using var context = new BunitContext();

        var component = context.Render<MainLayout>(parameters => parameters
            .Add(layout => layout.Body, (RenderFragment)(builder => builder.AddMarkupContent(0, "<p>Body</p>"))));

        component.Markup.ShouldNotContain("Approval required");
        component.Markup.ShouldNotContain("Scans never modify files");
    }

    [Fact]
    public void Legacy_history_route_redirects_to_quarantine()
    {
        using var context = new BunitContext();
        var navigation = context.Services.GetRequiredService<NavigationManager>();

        context.Render<HistoryRedirect>();

        navigation.Uri.ShouldEndWith("/history/quarantine");
    }

    [Fact]
    public void Quarantine_does_not_navigate_during_initial_load()
    {
        using var factory = new TestDbFactory();
        using var context = CreateContext(factory);

        context.Render<Quarantine>();

        context.Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("/");
    }

    private static BunitContext CreateContext(TestDbFactory factory)
    {
        var context = new BunitContext();
        context.Services.AddSingleton<IDbContextFactory<SubtitleCleanupDbContext>>(factory);
        context.Services.AddSingleton(new ChangeExecutionService(
            factory,
            new PhysicalSubtitleFileSystem(),
            Options.Create(new SubtitleCleanupOptions { QuarantineRoot = Path.GetTempPath() }),
            new SystemClock(),
            new OperationGate(),
            NullLogger<ChangeExecutionService>.Instance));
        return context;
    }

    private static async Task SeedOperationsAsync(
        TestDbFactory factory,
        int count,
        OperationType type = OperationType.Rename,
        OperationStatus status = OperationStatus.Applied)
    {
        await using var db = factory.CreateDbContext();
        for (var index = 0; index < count; index++)
        {
            db.FileOperations.Add(new FileOperationRecord
            {
                Type = type,
                Status = status,
                SourcePath = $"/media/file-{index}.srt",
                DestinationPath = $"/media/destination-{index}.srt",
                Sha256 = $"hash-{index}",
                OccurredUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index)
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task SeedScansAsync(TestDbFactory factory, int count)
    {
        await using var db = factory.CreateDbContext();
        for (var index = 0; index < count; index++)
        {
            db.ScanRuns.Add(new ScanRun
            {
                StartedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index),
                Status = "Completed",
                DiscoveredCount = index,
                ProposedCount = index / 2
            });
        }

        await db.SaveChangesAsync();
    }
}
