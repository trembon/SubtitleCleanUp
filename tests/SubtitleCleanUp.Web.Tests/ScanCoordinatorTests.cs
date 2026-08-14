using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using SubtitleCleanUp.Core.Abstractions;
using SubtitleCleanUp.Core.Configuration;
using SubtitleCleanUp.Core.Models;
using SubtitleCleanUp.Core.Services;
using SubtitleCleanUp.Web.Services;

namespace SubtitleCleanUp.Web.Tests;

public sealed class ScanCoordinatorTests
{
    [Fact]
    public async Task ScanAsync_persists_proposal_and_reuses_it_when_fingerprints_match()
    {
        using var directory = new TemporaryDirectory();
        using var factory = new TestDbFactory();
        var scanner = Substitute.For<ISubtitleScanner>();
        var planner = Substitute.For<IChangePlanner>();
        var clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.Zero));
        var sourcePath = Path.Combine(directory.Path, "Movie.en.srt");
        await File.WriteAllTextAsync(sourcePath, new string('A', 100));
        var info = new FileInfo(sourcePath);
        var fileSystem = new PhysicalSubtitleFileSystem();
        var file = new DiscoveredSubtitle(
            "media", directory.Path, sourcePath, "Movie.en.srt", "Movie.en.srt",
            "Movie", "eng", null, Path.Combine(directory.Path, "Movie.eng.srt"), false,
            new SubtitleFingerprint(info.Length, info.LastWriteTimeUtc,
                await fileSystem.ComputeSha256Async(sourcePath, CancellationToken.None)),
            null);
        var discovery = new SubtitleDiscovery([file], []);
        var draft = new ChangeProposalDraft(
            "group", "media", directory.Path, "Movie", "eng", null, Path.Combine(directory.Path, "Movie.eng.srt"),
            ProposalKind.Rename, "rename", [file], 0);
        scanner.DiscoverAsync(Arg.Any<IReadOnlyCollection<MediaRootOptions>>(), Arg.Any<CancellationToken>())
            .Returns(discovery, new SubtitleDiscovery([], []));
        planner.CreatePlan(discovery).Returns([draft]);
        planner.CreatePlan(Arg.Is<SubtitleDiscovery>(value => value.Files.Count == 0)).Returns([]);
        var executor = new ChangeExecutionService(
            factory,
            fileSystem,
            Options.Create(new SubtitleCleanupOptions { QuarantineRoot = Path.Combine(directory.Path, "quarantine") }),
            clock,
            new OperationGate(),
            NullLogger<ChangeExecutionService>.Instance);
        var coordinator = new ScanCoordinator(
            factory, scanner, planner,
            Options.Create(new SubtitleCleanupOptions
            {
                Roots = [new MediaRootOptions { Name = "media", Path = "/media" }]
            }),
            clock, new OperationGate(), executor, NullLogger<ScanCoordinator>.Instance);

        await coordinator.ScanAsync();
        await coordinator.ScanAsync();

        await using var db = factory.CreateDbContext();
        db.ChangeProposals.Count().ShouldBe(1);
        db.ScanRuns.Count().ShouldBe(2);
        var proposal = db.ChangeProposals.Single();
        proposal.SelectedKeeperId.ShouldNotBeNull();
        proposal.Status.ShouldBe(ProposalStatus.Applied);
    }
}
