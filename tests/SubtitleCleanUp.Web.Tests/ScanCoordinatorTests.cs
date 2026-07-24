using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using SubtitleCleanUp.Core.Abstractions;
using SubtitleCleanUp.Core.Configuration;
using SubtitleCleanUp.Core.Models;
using SubtitleCleanUp.Web.Services;

namespace SubtitleCleanUp.Web.Tests;

public sealed class ScanCoordinatorTests
{
    [Fact]
    public async Task ScanAsync_persists_proposal_and_reuses_it_when_fingerprints_match()
    {
        using var factory = new TestDbFactory();
        var scanner = Substitute.For<ISubtitleScanner>();
        var planner = Substitute.For<IChangePlanner>();
        var clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.Zero));
        var file = new DiscoveredSubtitle(
            "media", "/media", "/media/Movie.en.srt", "Movie.en.srt", "Movie.en.srt",
            "Movie", "eng", null, "/media/Movie.eng.srt", false,
            new SubtitleFingerprint(100, new DateTime(2026, 7, 24, 9, 0, 0, DateTimeKind.Utc), "ABC"),
            null);
        var discovery = new SubtitleDiscovery([file], []);
        var draft = new ChangeProposalDraft(
            "group", "media", "/media", "Movie", "eng", null, "/media/Movie.eng.srt",
            ProposalKind.Rename, "rename", [file], 0);
        scanner.DiscoverAsync(Arg.Any<IReadOnlyCollection<MediaRootOptions>>(), Arg.Any<CancellationToken>())
            .Returns(discovery);
        planner.CreatePlan(discovery).Returns([draft]);
        var coordinator = new ScanCoordinator(
            factory, scanner, planner,
            Options.Create(new SubtitleCleanupOptions
            {
                Roots = [new MediaRootOptions { Name = "media", Path = "/media" }]
            }),
            clock, new OperationGate(), NullLogger<ScanCoordinator>.Instance);

        await coordinator.ScanAsync();
        await coordinator.ScanAsync();

        await using var db = factory.CreateDbContext();
        db.ChangeProposals.Count().ShouldBe(1);
        db.ScanRuns.Count().ShouldBe(2);
        var proposal = db.ChangeProposals.Single();
        proposal.SelectedKeeperId.ShouldNotBeNull();
        proposal.Status.ShouldBe(ProposalStatus.Pending);
    }
}
