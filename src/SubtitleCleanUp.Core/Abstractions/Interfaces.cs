using SubtitleCleanUp.Core.Configuration;
using SubtitleCleanUp.Core.Models;

namespace SubtitleCleanUp.Core.Abstractions;

public interface ISubtitleFilenameParser
{
    ParsedSubtitleName? Parse(string subtitleFileName, IReadOnlyCollection<string> mediaStems);
    ManualSubtitleName? Analyze(string subtitleFileName, IReadOnlyCollection<string> mediaStems);
}

public interface ISubtitleFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    IEnumerable<string> EnumerateDirectories(string path);
    IEnumerable<string> EnumerateFiles(string path);
    FileInfo GetFileInfo(string path);
    Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken);
    Task<byte[]> ReadBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken);
    Task CopyAsync(string source, string destination, CancellationToken cancellationToken);
    void Move(string source, string destination);
    void Delete(string path);
    void CreateDirectory(string path);
}

public interface ISubtitleScanner
{
    Task<SubtitleDiscovery> DiscoverAsync(
        IReadOnlyCollection<MediaRootOptions> roots,
        CancellationToken cancellationToken);
}

public interface IChangePlanner
{
    IReadOnlyList<ChangeProposalDraft> CreatePlan(SubtitleDiscovery discovery);
}

public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
}
