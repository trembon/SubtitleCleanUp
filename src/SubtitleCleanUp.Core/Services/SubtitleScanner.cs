using SubtitleCleanUp.Core.Abstractions;
using SubtitleCleanUp.Core.Configuration;
using SubtitleCleanUp.Core.Models;

namespace SubtitleCleanUp.Core.Services;

public sealed class SubtitleScanner(
    ISubtitleFileSystem fileSystem,
    ISubtitleFilenameParser parser) : ISubtitleScanner
{
    private static readonly HashSet<string> VideoExtensions = new(
        [".mkv", ".mp4", ".avi", ".mov", ".m4v", ".webm", ".wmv", ".ts", ".m2ts"],
        StringComparer.OrdinalIgnoreCase);

    public async Task<SubtitleDiscovery> DiscoverAsync(
        IReadOnlyCollection<MediaRootOptions> roots,
        CancellationToken cancellationToken)
    {
        var discovered = new List<DiscoveredSubtitle>();
        var errors = new List<string>();

        foreach (var root in roots)
        {
            if (!fileSystem.DirectoryExists(root.Path))
            {
                errors.Add($"{root.Name}: directory '{root.Path}' is unavailable.");
                continue;
            }

            var queue = new Queue<string>();
            queue.Enqueue(Path.GetFullPath(root.Path));
            while (queue.TryDequeue(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    foreach (var child in fileSystem.EnumerateDirectories(directory))
                    {
                        var info = new DirectoryInfo(child);
                        if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            queue.Enqueue(child);
                        }
                    }

                    var files = fileSystem.EnumerateFiles(directory).ToArray();
                    var mediaStems = files
                        .Where(x => VideoExtensions.Contains(Path.GetExtension(x)))
                        .Select(Path.GetFileNameWithoutExtension)
                        .Where(x => x is not null)
                        .Cast<string>()
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    foreach (var path in files.Where(x =>
                                 Path.GetExtension(x).Equals(".srt", StringComparison.OrdinalIgnoreCase)))
                    {
                        var info = fileSystem.GetFileInfo(path);
                        var relative = Path.GetRelativePath(root.Path, path);
                        var parsed = parser.Parse(info.Name, mediaStems);
                        var manual = parsed is null ? parser.Analyze(info.Name, mediaStems) : null;
                        var fingerprint = new SubtitleFingerprint(
                            info.Length,
                            info.LastWriteTimeUtc,
                            await fileSystem.ComputeSha256Async(path, cancellationToken));
                        discovered.Add(new DiscoveredSubtitle(
                            root.Name,
                            Path.GetFullPath(root.Path),
                            Path.GetFullPath(path),
                            relative,
                            info.Name,
                            parsed?.MediaStem ?? manual?.MediaStem,
                            parsed?.Language ?? manual?.Language,
                            parsed?.Variant ?? manual?.Variant,
                            parsed is null
                                ? manual?.CanonicalFileName is null ? null : Path.Combine(directory, manual.CanonicalFileName)
                                : Path.Combine(directory, parsed.CanonicalFileName),
                            parsed?.IsCanonical ?? false,
                            fingerprint,
                            parsed is null
                                ? "No unambiguous matching video and supported language suffix were found."
                                : null));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{root.Name}: could not scan '{directory}': {ex.Message}");
                }
            }
        }

        return new SubtitleDiscovery(discovered, errors);
    }
}
