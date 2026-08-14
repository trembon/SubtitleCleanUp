using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using SubtitleCleanUp.Core.Abstractions;
using SubtitleCleanUp.Core.Configuration;
using SubtitleCleanUp.Core.Models;
using SubtitleCleanUp.Core.Services;
using SubtitleCleanUp.Web.Data;
using SubtitleCleanUp.Web.Services;

namespace SubtitleCleanUp.Web.Tests;

public sealed class ChangeExecutionServiceTests
{
    [Fact]
    public async Task Apply_quarantines_duplicates_and_renames_selected_keeper()
    {
        using var directory = new TemporaryDirectory();
        using var factory = new TestDbFactory();
        var proposalId = await SeedDuplicateAsync(factory, directory.Path);
        var service = CreateService(factory, directory.Path);

        await service.ApplyAsync(proposalId);

        File.Exists(Path.Combine(directory.Path, "Movie.eng.srt")).ShouldBeTrue();
        File.Exists(Path.Combine(directory.Path, "Movie.en.srt")).ShouldBeFalse();
        File.Exists(Path.Combine(directory.Path, "Movie.en2.srt")).ShouldBeFalse();
        using var db = factory.CreateDbContext();
        var proposal = db.ChangeProposals.Single(x => x.Id == proposalId);
        proposal.Status.ShouldBe(ProposalStatus.Applied);
        var quarantine = db.FileOperations.Single(x => x.Type == OperationType.Quarantine);
        quarantine.Status.ShouldBe(OperationStatus.Applied);
        File.Exists(quarantine.DestinationPath).ShouldBeTrue();
        db.FileOperations.Count(x => x.Type == OperationType.Rename).ShouldBe(1);
    }

    [Fact]
    public async Task Restore_returns_quarantined_file_without_overwriting()
    {
        using var directory = new TemporaryDirectory();
        using var factory = new TestDbFactory();
        var proposalId = await SeedDuplicateAsync(factory, directory.Path);
        var service = CreateService(factory, directory.Path);
        await service.ApplyAsync(proposalId);
        int operationId;
        using (var db = factory.CreateDbContext())
        {
            operationId = db.FileOperations.Single(x => x.Type == OperationType.Quarantine).Id;
        }

        await service.RestoreAsync(operationId);

        File.Exists(Path.Combine(directory.Path, "Movie.en.srt")).ShouldBeTrue();
        using var verification = factory.CreateDbContext();
        verification.FileOperations.Single(x => x.Id == operationId).Status.ShouldBe(OperationStatus.Restored);
    }

    [Fact]
    public async Task Apply_marks_proposal_stale_when_file_changed_after_scan()
    {
        using var directory = new TemporaryDirectory();
        using var factory = new TestDbFactory();
        var proposalId = await SeedDuplicateAsync(factory, directory.Path);
        var service = CreateService(factory, directory.Path);
        await File.AppendAllTextAsync(Path.Combine(directory.Path, "Movie.en.srt"), "changed");

        await Should.ThrowAsync<InvalidOperationException>(() => service.ApplyAsync(proposalId));

        using var db = factory.CreateDbContext();
        db.ChangeProposals.Single(x => x.Id == proposalId).Status.ShouldBe(ProposalStatus.Stale);
        File.Exists(Path.Combine(directory.Path, "Movie.en.srt")).ShouldBeTrue();
    }

    [Fact]
    public async Task ApplyPendingRenames_applies_only_rename_proposals()
    {
        using var directory = new TemporaryDirectory();
        using var factory = new TestDbFactory();
        var duplicateId = await SeedDuplicateAsync(factory, directory.Path);
        var renameId = await SeedRenameAsync(factory, directory.Path);
        var service = CreateService(factory, directory.Path);

        var result = await service.ApplyPendingRenamesAsync();

        result.Applied.ShouldBe(1);
        result.Stale.ShouldBe(0);
        result.Failed.ShouldBe(0);
        File.Exists(Path.Combine(directory.Path, "Movie.eng.srt")).ShouldBeTrue();
        using var db = factory.CreateDbContext();
        db.ChangeProposals.Single(x => x.Id == renameId).Status.ShouldBe(ProposalStatus.Applied);
        db.ChangeProposals.Single(x => x.Id == duplicateId).Status.ShouldBe(ProposalStatus.Pending);
    }

    private static ChangeExecutionService CreateService(TestDbFactory factory, string directory)
    {
        var clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        return new ChangeExecutionService(
            factory,
            new PhysicalSubtitleFileSystem(),
            Options.Create(new SubtitleCleanupOptions
            {
                QuarantineRoot = Path.Combine(directory, "quarantine")
            }),
            clock,
            new OperationGate(),
            NullLogger<ChangeExecutionService>.Instance);
    }

    private static async Task<int> SeedDuplicateAsync(TestDbFactory factory, string directory)
    {
        var smallPath = Path.Combine(directory, "Movie.en.srt");
        var largePath = Path.Combine(directory, "Movie.en2.srt");
        await File.WriteAllTextAsync(smallPath, "small");
        await File.WriteAllTextAsync(largePath, "this subtitle is larger");
        var fileSystem = new PhysicalSubtitleFileSystem();
        var smallInfo = new FileInfo(smallPath);
        var largeInfo = new FileInfo(largePath);

        await using var db = factory.CreateDbContext();
        var proposal = new ChangeProposal
        {
            GroupKey = "media|Movie|eng",
            FingerprintSignature = "signature",
            RootName = "media",
            DirectoryPath = directory,
            MediaStem = "Movie",
            Language = "eng",
            CanonicalPath = Path.Combine(directory, "Movie.eng.srt"),
            Kind = ProposalKind.Duplicate,
            Status = ProposalStatus.Pending,
            Reason = "duplicates",
            CreatedUtc = DateTimeOffset.UtcNow,
            LastSeenUtc = DateTimeOffset.UtcNow,
            Files =
            [
                new SubtitleFileRecord
                {
                    RootName = "media", RootPath = directory, FullPath = smallPath,
                    RelativePath = "Movie.en.srt", FileName = "Movie.en.srt",
                    Size = smallInfo.Length, LastWriteUtc = smallInfo.LastWriteTimeUtc,
                    Sha256 = await fileSystem.ComputeSha256Async(smallPath, CancellationToken.None)
                },
                new SubtitleFileRecord
                {
                    RootName = "media", RootPath = directory, FullPath = largePath,
                    RelativePath = "Movie.en2.srt", FileName = "Movie.en2.srt",
                    Size = largeInfo.Length, LastWriteUtc = largeInfo.LastWriteTimeUtc,
                    Sha256 = await fileSystem.ComputeSha256Async(largePath, CancellationToken.None),
                    IsRecommended = true
                }
            ]
        };
        db.ChangeProposals.Add(proposal);
        await db.SaveChangesAsync();
        proposal.SelectedKeeperId = proposal.Files[1].Id;
        await db.SaveChangesAsync();
        return proposal.Id;
    }

    private static async Task<int> SeedRenameAsync(TestDbFactory factory, string directory)
    {
        var sourcePath = Path.Combine(directory, "Movie.en.srt");
        await File.WriteAllTextAsync(sourcePath, "subtitle");
        var fileSystem = new PhysicalSubtitleFileSystem();
        var info = new FileInfo(sourcePath);
        await using var db = factory.CreateDbContext();
        var proposal = new ChangeProposal
        {
            GroupKey = "media|Movie|eng",
            FingerprintSignature = "rename-signature",
            RootName = "media",
            DirectoryPath = directory,
            MediaStem = "Movie",
            Language = "eng",
            CanonicalPath = Path.Combine(directory, "Movie.eng.srt"),
            Kind = ProposalKind.Rename,
            Status = ProposalStatus.Pending,
            Reason = "rename",
            CreatedUtc = DateTimeOffset.UtcNow,
            LastSeenUtc = DateTimeOffset.UtcNow,
            Files =
            [
                new SubtitleFileRecord
                {
                    RootName = "media", RootPath = directory, FullPath = sourcePath,
                    RelativePath = "Movie.en.srt", FileName = "Movie.en.srt",
                    Size = info.Length, LastWriteUtc = info.LastWriteTimeUtc,
                    Sha256 = await fileSystem.ComputeSha256Async(sourcePath, CancellationToken.None),
                    IsRecommended = true
                }
            ]
        };
        db.ChangeProposals.Add(proposal);
        await db.SaveChangesAsync();
        proposal.SelectedKeeperId = proposal.Files[0].Id;
        await db.SaveChangesAsync();
        return proposal.Id;
    }
}
