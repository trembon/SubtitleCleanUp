namespace SubtitleCleanUp.Core.Models;

public enum ProposalKind
{
    Rename,
    Duplicate,
    ManualReview
}

public enum ProposalStatus
{
    Pending,
    Dismissed,
    Stale,
    Applying,
    Applied,
    Failed
}

public enum OperationType
{
    Rename,
    Quarantine
}

public enum OperationStatus
{
    Applied,
    Failed,
    Restored,
    Purged
}

public sealed record ParsedSubtitleName(
    string MediaStem,
    string Language,
    string? Variant,
    string CanonicalFileName,
    bool IsCanonical);

public sealed record SubtitleFingerprint(long Size, DateTime LastWriteUtc, string Sha256);

public sealed record DiscoveredSubtitle(
    string RootName,
    string RootPath,
    string FullPath,
    string RelativePath,
    string FileName,
    string? MediaStem,
    string? Language,
    string? Variant,
    string? CanonicalPath,
    bool IsCanonical,
    SubtitleFingerprint Fingerprint,
    string? Issue);

public sealed record ChangeProposalDraft(
    string GroupKey,
    string RootName,
    string DirectoryPath,
    string MediaStem,
    string? Language,
    string? Variant,
    string? CanonicalPath,
    ProposalKind Kind,
    string Reason,
    IReadOnlyList<DiscoveredSubtitle> Files,
    int RecommendedIndex)
{
    public string FingerprintSignature => string.Join(
        '|',
        Files.OrderBy(x => x.FullPath, StringComparer.Ordinal)
            .Select(x => $"{x.RelativePath}:{x.Fingerprint.Size}:{x.Fingerprint.LastWriteUtc.Ticks}:{x.Fingerprint.Sha256}"));
}

public sealed record SubtitleDiscovery(
    IReadOnlyList<DiscoveredSubtitle> Files,
    IReadOnlyList<string> Errors);

public sealed record PreviewContent(string Text, string EncodingName, bool IsTruncated);
