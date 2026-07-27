using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using SubtitleCleanUp.Core.Models;
using SubtitleCleanUp.Web.Data;

namespace SubtitleCleanUp.Web.Tests;

[CollectionDefinition(nameof(QueueApiTestCollection), DisableParallelization = true)]
public sealed class QueueApiTestCollection;

[Collection(nameof(QueueApiTestCollection))]
public sealed class QueueApiTests
{
    [Fact]
    public async Task GetQueue_returns_zero_as_json_when_queue_is_empty()
    {
        using var application = new QueueApiApplication();
        using var client = application.CreateClient();

        var response = await client.GetAsync("/api/queue");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        var payload = await response.Content.ReadFromJsonAsync<QueueCountResponse>();
        payload.ShouldNotBeNull();
        payload.Count.ShouldBe(0);
    }

    [Fact]
    public async Task GetQueue_counts_pending_proposals_once_and_excludes_other_statuses()
    {
        using var application = new QueueApiApplication();
        using var client = application.CreateClient();
        var dbFactory = application.Services
            .GetRequiredService<IDbContextFactory<SubtitleCleanupDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.ChangeProposals.AddRange(
                Proposal(ProposalKind.Rename, ProposalStatus.Pending),
                Proposal(ProposalKind.Duplicate, ProposalStatus.Pending, fileCount: 2),
                Proposal(ProposalKind.ManualReview, ProposalStatus.Pending),
                Proposal(ProposalKind.Rename, ProposalStatus.Dismissed),
                Proposal(ProposalKind.Rename, ProposalStatus.Stale),
                Proposal(ProposalKind.Rename, ProposalStatus.Applying),
                Proposal(ProposalKind.Rename, ProposalStatus.Applied),
                Proposal(ProposalKind.Rename, ProposalStatus.Failed));
            await db.SaveChangesAsync();
        }

        var payload = await client.GetFromJsonAsync<QueueCountResponse>("/api/queue");

        payload.ShouldNotBeNull();
        payload.Count.ShouldBe(3);
    }

    private static ChangeProposal Proposal(
        ProposalKind kind,
        ProposalStatus status,
        int fileCount = 0) =>
        new()
        {
            GroupKey = Guid.NewGuid().ToString("N"),
            FingerprintSignature = Guid.NewGuid().ToString("N"),
            RootName = "media",
            DirectoryPath = "/media",
            MediaStem = "Movie",
            Kind = kind,
            Status = status,
            Reason = "test",
            CreatedUtc = DateTimeOffset.UtcNow,
            LastSeenUtc = DateTimeOffset.UtcNow,
            Files = Enumerable.Range(0, fileCount)
                .Select(index => new SubtitleFileRecord
                {
                    RootName = "media",
                    RootPath = "/media",
                    FullPath = $"/media/Movie.{index}.eng.srt",
                    RelativePath = $"Movie.{index}.eng.srt",
                    FileName = $"Movie.{index}.eng.srt",
                    Sha256 = Guid.NewGuid().ToString("N")
                })
                .ToList()
        };

    private sealed record QueueCountResponse(int Count);
}

internal sealed class QueueApiApplication : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public QueueApiApplication()
    {
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextFactory<SubtitleCleanupDbContext>>();
            services.RemoveAll<DbContextOptions<SubtitleCleanupDbContext>>();
            services.AddDbContextFactory<SubtitleCleanupDbContext>(options =>
                options.UseSqlite(_connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}
