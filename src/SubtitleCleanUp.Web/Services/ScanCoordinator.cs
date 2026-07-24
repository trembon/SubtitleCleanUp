using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SubtitleCleanUp.Core.Abstractions;
using SubtitleCleanUp.Core.Configuration;
using SubtitleCleanUp.Core.Models;
using SubtitleCleanUp.Web.Data;

namespace SubtitleCleanUp.Web.Services;

public sealed class ScanCoordinator(
    IDbContextFactory<SubtitleCleanupDbContext> dbFactory,
    ISubtitleScanner scanner,
    IChangePlanner planner,
    IOptions<SubtitleCleanupOptions> options,
    ISystemClock clock,
    OperationGate gate,
    ILogger<ScanCoordinator> logger)
{
    public async Task<int> ScanAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await gate.EnterAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var run = new ScanRun { StartedUtc = clock.UtcNow };
        db.ScanRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var discovery = await scanner.DiscoverAsync(options.Value.Roots, cancellationToken);
            var drafts = planner.CreatePlan(discovery);
            var now = clock.UtcNow;
            var active = await db.ChangeProposals
                .Include(x => x.Files)
                .Where(x => x.Status == ProposalStatus.Pending || x.Status == ProposalStatus.Dismissed)
                .ToListAsync(cancellationToken);
            var seenIds = new HashSet<int>();

            foreach (var draft in drafts)
            {
                var existing = active.FirstOrDefault(x =>
                    x.GroupKey == draft.GroupKey &&
                    x.FingerprintSignature == draft.FingerprintSignature);
                if (existing is not null)
                {
                    existing.LastSeenUtc = now;
                    existing.Reason = draft.Reason;
                    seenIds.Add(existing.Id);
                    continue;
                }

                foreach (var stale in active.Where(x => x.GroupKey == draft.GroupKey))
                {
                    stale.Status = ProposalStatus.Stale;
                }

                var proposal = MapDraft(draft, now);
                db.ChangeProposals.Add(proposal);
                await db.SaveChangesAsync(cancellationToken);
                proposal.SelectedKeeperId = proposal.Files.Single(x => x.IsRecommended).Id;
                seenIds.Add(proposal.Id);
            }

            foreach (var missing in active.Where(x => !seenIds.Contains(x.Id)))
            {
                missing.Status = ProposalStatus.Stale;
            }

            foreach (var message in discovery.Errors)
            {
                run.Issues.Add(new ScanIssue { Message = message });
            }

            run.DiscoveredCount = discovery.Files.Count;
            run.ProposedCount = drafts.Count;
            run.CompletedUtc = clock.UtcNow;
            run.Status = discovery.Errors.Count == 0 ? "Completed" : "CompletedWithWarnings";
            await db.SaveChangesAsync(cancellationToken);
            return run.Id;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Subtitle scan failed.");
            run.CompletedUtc = clock.UtcNow;
            run.Status = "Failed";
            run.Error = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private static ChangeProposal MapDraft(ChangeProposalDraft draft, DateTimeOffset now)
    {
        var proposal = new ChangeProposal
        {
            GroupKey = draft.GroupKey,
            FingerprintSignature = draft.FingerprintSignature,
            RootName = draft.RootName,
            DirectoryPath = draft.DirectoryPath,
            MediaStem = draft.MediaStem,
            Language = draft.Language,
            Variant = draft.Variant,
            CanonicalPath = draft.CanonicalPath,
            Kind = draft.Kind,
            Reason = draft.Reason,
            CreatedUtc = now,
            LastSeenUtc = now
        };
        proposal.Files = draft.Files.Select((file, index) => new SubtitleFileRecord
        {
            RootName = file.RootName,
            RootPath = file.RootPath,
            FullPath = file.FullPath,
            RelativePath = file.RelativePath,
            FileName = file.FileName,
            Size = file.Fingerprint.Size,
            LastWriteUtc = file.Fingerprint.LastWriteUtc,
            Sha256 = file.Fingerprint.Sha256,
            IsCanonical = file.IsCanonical,
            IsRecommended = index == draft.RecommendedIndex
        }).ToList();
        return proposal;
    }
}
