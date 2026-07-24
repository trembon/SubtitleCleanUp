using SubtitleCleanUp.Core.Abstractions;
using SubtitleCleanUp.Core.Models;

namespace SubtitleCleanUp.Core.Services;

public sealed class ChangePlanner : IChangePlanner
{
    public IReadOnlyList<ChangeProposalDraft> CreatePlan(SubtitleDiscovery discovery)
    {
        var drafts = new List<ChangeProposalDraft>();

        foreach (var file in discovery.Files.Where(x => x.Issue is not null))
        {
            drafts.Add(new ChangeProposalDraft(
                $"manual|{file.RootName}|{file.RelativePath}",
                file.RootName,
                Path.GetDirectoryName(file.FullPath) ?? file.RootPath,
                file.FileName,
                null,
                null,
                null,
                ProposalKind.ManualReview,
                file.Issue!,
                [file],
                0));
        }

        var groups = discovery.Files
            .Where(x => x.Issue is null)
            .GroupBy(x => string.Join(
                '|',
                x.RootName,
                Path.GetDirectoryName(x.RelativePath) ?? string.Empty,
                x.MediaStem,
                x.Language,
                x.Variant ?? string.Empty), StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var files = group.ToArray();
            if (files.Length == 1 && files[0].IsCanonical)
            {
                continue;
            }

            var recommended = files
                .Select((file, index) => (file, index))
                .OrderByDescending(x => x.file.Fingerprint.Size)
                .ThenByDescending(x => x.file.IsCanonical)
                .ThenByDescending(x => x.file.Fingerprint.LastWriteUtc)
                .ThenBy(x => x.file.FullPath, StringComparer.Ordinal)
                .First().index;
            var first = files[0];
            var kind = files.Length > 1 ? ProposalKind.Duplicate : ProposalKind.Rename;
            drafts.Add(new ChangeProposalDraft(
                group.Key,
                first.RootName,
                Path.GetDirectoryName(first.FullPath) ?? first.RootPath,
                first.MediaStem!,
                first.Language,
                first.Variant,
                first.CanonicalPath,
                kind,
                kind == ProposalKind.Duplicate
                    ? $"{files.Length} subtitles resolve to the same canonical name."
                    : "The language or variant suffix is not canonical.",
                files,
                recommended));
        }

        return drafts;
    }
}
