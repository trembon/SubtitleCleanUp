using SubtitleCleanUp.Core.Models;

namespace SubtitleCleanUp.Web.Data;

public sealed class ScanRun
{
    public int Id { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string Status { get; set; } = "Running";
    public int DiscoveredCount { get; set; }
    public int ProposedCount { get; set; }
    public string? Error { get; set; }
    public List<ScanIssue> Issues { get; set; } = [];
}

public sealed class ScanIssue
{
    public int Id { get; set; }
    public int ScanRunId { get; set; }
    public ScanRun ScanRun { get; set; } = null!;
    public string Message { get; set; } = string.Empty;
}

public sealed class ChangeProposal
{
    public int Id { get; set; }
    public string GroupKey { get; set; } = string.Empty;
    public string FingerprintSignature { get; set; } = string.Empty;
    public string RootName { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;
    public string MediaStem { get; set; } = string.Empty;
    public string? Language { get; set; }
    public string? Variant { get; set; }
    public string? CanonicalPath { get; set; }
    public ProposalKind Kind { get; set; }
    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public int? SelectedKeeperId { get; set; }
    public string? FailureMessage { get; set; }
    public List<SubtitleFileRecord> Files { get; set; } = [];
    public List<FileOperationRecord> Operations { get; set; } = [];
}

public sealed class SubtitleFileRecord
{
    public int Id { get; set; }
    public int ChangeProposalId { get; set; }
    public ChangeProposal Proposal { get; set; } = null!;
    public string RootName { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool IsCanonical { get; set; }
    public bool IsRecommended { get; set; }
}

public sealed class FileOperationRecord
{
    public int Id { get; set; }
    public int? ChangeProposalId { get; set; }
    public ChangeProposal? Proposal { get; set; }
    public int? SubtitleFileRecordId { get; set; }
    public OperationType Type { get; set; }
    public OperationStatus Status { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public DateTimeOffset OccurredUtc { get; set; }
    public string? Error { get; set; }
}
