using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SubtitleCleanUp.Core.Abstractions;
using SubtitleCleanUp.Core.Configuration;
using SubtitleCleanUp.Core.Models;
using SubtitleCleanUp.Web.Data;

namespace SubtitleCleanUp.Web.Services;

public sealed class ChangeExecutionService(
    IDbContextFactory<SubtitleCleanupDbContext> dbFactory,
    ISubtitleFileSystem fileSystem,
    IOptions<SubtitleCleanupOptions> options,
    ISystemClock clock,
    OperationGate gate,
    ILogger<ChangeExecutionService> logger)
{
    public async Task SelectKeeperAsync(int proposalId, int fileId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var proposal = await db.ChangeProposals.Include(x => x.Files)
            .SingleAsync(x => x.Id == proposalId, cancellationToken);
        if (proposal.Status != ProposalStatus.Pending || proposal.Files.All(x => x.Id != fileId))
        {
            throw new InvalidOperationException("The selected keeper is not part of an active proposal.");
        }

        proposal.SelectedKeeperId = fileId;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DismissAsync(int proposalId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var proposal = await db.ChangeProposals.SingleAsync(x => x.Id == proposalId, cancellationToken);
        if (proposal.Status == ProposalStatus.Pending)
        {
            proposal.Status = ProposalStatus.Dismissed;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task ApplyAsync(int proposalId, CancellationToken cancellationToken = default)
    {
        using var lease = await gate.EnterAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var proposal = await db.ChangeProposals
            .Include(x => x.Files)
            .SingleAsync(x => x.Id == proposalId, cancellationToken);
        await ApplyProposalAsync(db, proposal, cancellationToken);
    }

    public async Task<AutomaticRenameResult> ApplyPendingRenamesAsync(
        CancellationToken cancellationToken = default)
    {
        using var lease = await gate.EnterAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var proposals = await db.ChangeProposals
            .Include(x => x.Files)
            .Where(x => x.Status == ProposalStatus.Pending && x.Kind == ProposalKind.Rename)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var result = new AutomaticRenameResult();

        foreach (var proposal in proposals)
        {
            try
            {
                await ApplyProposalAsync(db, proposal, cancellationToken);
                result.Applied++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                if (proposal.Status == ProposalStatus.Stale)
                {
                    result.Stale++;
                }
                else
                {
                    result.Failed++;
                }

                logger.LogWarning(ex, "Automatic rename proposal {ProposalId} was not applied.", proposal.Id);
            }
        }

        return result;
    }

    private async Task ApplyProposalAsync(
        SubtitleCleanupDbContext db,
        ChangeProposal proposal,
        CancellationToken cancellationToken)
    {
        if (proposal.Status != ProposalStatus.Pending || proposal.Kind == ProposalKind.ManualReview)
        {
            throw new InvalidOperationException("Only pending rename and duplicate proposals can be applied.");
        }

        var keeper = proposal.Files.SingleOrDefault(x => x.Id == proposal.SelectedKeeperId)
            ?? throw new InvalidOperationException("Choose a subtitle to keep before applying this proposal.");

        try
        {
            await ValidateAsync(proposal, cancellationToken);
            proposal.Status = ProposalStatus.Applying;
            proposal.FailureMessage = null;
            await db.SaveChangesAsync(cancellationToken);

            foreach (var duplicate in proposal.Files.Where(x => x.Id != keeper.Id))
            {
                var destination = BuildQuarantinePath(duplicate);
                fileSystem.CreateDirectory(Path.GetDirectoryName(destination)!);
                await fileSystem.CopyAsync(duplicate.FullPath, destination, cancellationToken);
                var copiedHash = await fileSystem.ComputeSha256Async(destination, cancellationToken);
                if (!copiedHash.Equals(duplicate.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    fileSystem.Delete(destination);
                    throw new IOException($"The quarantine copy of '{duplicate.FileName}' failed hash verification.");
                }

                try
                {
                    fileSystem.Delete(duplicate.FullPath);
                }
                catch
                {
                    if (fileSystem.FileExists(destination))
                    {
                        fileSystem.Delete(destination);
                    }

                    throw;
                }
                proposal.Operations.Add(new FileOperationRecord
                {
                    SubtitleFileRecordId = duplicate.Id,
                    Type = OperationType.Quarantine,
                    Status = OperationStatus.Applied,
                    SourcePath = duplicate.FullPath,
                    DestinationPath = destination,
                    Sha256 = duplicate.Sha256,
                    OccurredUtc = clock.UtcNow
                });
                await db.SaveChangesAsync(cancellationToken);
            }

            if (!string.Equals(keeper.FullPath, proposal.CanonicalPath, StringComparison.Ordinal))
            {
                Rename(keeper.FullPath, proposal.CanonicalPath!);
                proposal.Operations.Add(new FileOperationRecord
                {
                    SubtitleFileRecordId = keeper.Id,
                    Type = OperationType.Rename,
                    Status = OperationStatus.Applied,
                    SourcePath = keeper.FullPath,
                    DestinationPath = proposal.CanonicalPath!,
                    Sha256 = keeper.Sha256,
                    OccurredUtc = clock.UtcNow
                });
            }

            proposal.Status = ProposalStatus.Applied;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (StaleProposalException ex)
        {
            proposal.Status = ProposalStatus.Stale;
            proposal.FailureMessage = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply proposal {ProposalId}.", proposal.Id);
            proposal.Status = ProposalStatus.Failed;
            proposal.FailureMessage = ex.Message;
            proposal.Operations.Add(new FileOperationRecord
            {
                Type = OperationType.Rename,
                Status = OperationStatus.Failed,
                SourcePath = keeper.FullPath,
                DestinationPath = proposal.CanonicalPath ?? string.Empty,
                Sha256 = keeper.Sha256,
                OccurredUtc = clock.UtcNow,
                Error = ex.Message
            });
            await db.SaveChangesAsync(CancellationToken.None);
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    public async Task RestoreAsync(int operationId, CancellationToken cancellationToken = default)
    {
        using var lease = await gate.EnterAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var operation = await db.FileOperations.SingleAsync(x => x.Id == operationId, cancellationToken);
        if (operation.Type != OperationType.Quarantine || operation.Status != OperationStatus.Applied)
        {
            throw new InvalidOperationException("Only quarantined files can be restored.");
        }

        if (fileSystem.FileExists(operation.SourcePath))
        {
            throw new IOException($"Cannot restore because '{operation.SourcePath}' already exists.");
        }

        fileSystem.CreateDirectory(Path.GetDirectoryName(operation.SourcePath)!);
        await fileSystem.CopyAsync(operation.DestinationPath, operation.SourcePath, cancellationToken);
        var restoredHash = await fileSystem.ComputeSha256Async(operation.SourcePath, cancellationToken);
        if (!restoredHash.Equals(operation.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            fileSystem.Delete(operation.SourcePath);
            throw new IOException("The restored file failed hash verification.");
        }

        fileSystem.Delete(operation.DestinationPath);
        operation.Status = OperationStatus.Restored;
        operation.OccurredUtc = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task PurgeAsync(int operationId, CancellationToken cancellationToken = default)
    {
        using var lease = await gate.EnterAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var operation = await db.FileOperations.SingleAsync(x => x.Id == operationId, cancellationToken);
        if (operation.Type != OperationType.Quarantine || operation.Status != OperationStatus.Applied)
        {
            throw new InvalidOperationException("Only quarantined files can be purged.");
        }

        if (fileSystem.FileExists(operation.DestinationPath))
        {
            fileSystem.Delete(operation.DestinationPath);
        }

        operation.Status = OperationStatus.Purged;
        operation.OccurredUtc = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ValidateAsync(ChangeProposal proposal, CancellationToken cancellationToken)
    {
        foreach (var file in proposal.Files)
        {
            if (!fileSystem.FileExists(file.FullPath))
            {
                throw new StaleProposalException($"'{file.FileName}' no longer exists. Run a new scan.");
            }

            var info = fileSystem.GetFileInfo(file.FullPath);
            var hash = await fileSystem.ComputeSha256Async(file.FullPath, cancellationToken);
            if (info.Length != file.Size ||
                info.LastWriteTimeUtc != file.LastWriteUtc ||
                !hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new StaleProposalException($"'{file.FileName}' changed after it was scanned.");
            }
        }

        var sourcePaths = proposal.Files.Select(x => x.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (proposal.CanonicalPath is null ||
            (fileSystem.FileExists(proposal.CanonicalPath) && !sourcePaths.Contains(proposal.CanonicalPath)))
        {
            throw new IOException("The canonical destination is occupied by a file outside this proposal.");
        }
    }

    private string BuildQuarantinePath(SubtitleFileRecord file)
    {
        var relativeDirectory = Path.GetDirectoryName(file.RelativePath) ?? string.Empty;
        return Path.Combine(
            options.Value.QuarantineRoot,
            SanitizeSegment(file.RootName),
            relativeDirectory,
            $"{Path.GetFileName(file.FullPath)}.{Guid.NewGuid():N}.quarantine");
    }

    private void Rename(string source, string destination)
    {
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            var temporary = source + $".subtitlecleanup-{Guid.NewGuid():N}.tmp";
            fileSystem.Move(source, temporary);
            try
            {
                fileSystem.Move(temporary, destination);
            }
            catch
            {
                if (fileSystem.FileExists(temporary) && !fileSystem.FileExists(source))
                {
                    fileSystem.Move(temporary, source);
                }

                throw;
            }
            return;
        }

        fileSystem.Move(source, destination);
    }

    private static string SanitizeSegment(string value) =>
        string.Concat(value.Select(x => Path.GetInvalidFileNameChars().Contains(x) ? '_' : x));

    private sealed class StaleProposalException(string message) : Exception(message);
}

public sealed class AutomaticRenameResult
{
    public int Applied { get; set; }
    public int Stale { get; set; }
    public int Failed { get; set; }
}
