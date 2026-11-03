using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SubtitleCleanUp.Core.Abstractions;
using SubtitleCleanUp.Core.Configuration;
using SubtitleCleanUp.Core.Models;
using SubtitleCleanUp.Core.Services;
using SubtitleCleanUp.Web.Data;

namespace SubtitleCleanUp.Web.Services;

public enum ContentCleanupStatus { Changed, Unchanged, Skipped, Failed, Updated }

public sealed record ContentCleanupFolder(string RootName, string RelativePath);

public sealed record ContentCleanupPreview(
    string FileName,
    ContentCleanupStatus Status,
    string? Message = null,
    SubtitleFingerprint? Fingerprint = null,
    IReadOnlyDictionary<SrtCleanupReason, int>? Counts = null,
    IReadOnlyList<SrtCleanupExample>? Examples = null,
    int PossibleKaraokeCues = 0);

public sealed class ContentCleanupService(
    IDbContextFactory<SubtitleCleanupDbContext> dbFactory,
    ISubtitleFileSystem fileSystem,
    IOptions<SubtitleCleanupOptions> options,
    OperationGate gate,
    ISystemClock clock,
    ILogger<ContentCleanupService> logger)
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private string BackupDirectory => Path.GetFullPath(Path.Combine(options.Value.QuarantineRoot, "content-cleanup"));

    public IReadOnlyList<string> RootNames => options.Value.Roots.Select(root => root.Name).ToArray();

    public IReadOnlyList<string> ListFolders(ContentCleanupFolder folder)
    {
        var directory = ResolveFolder(folder);
        return fileSystem.EnumerateDirectories(directory)
            .Where(path => IsDirectChild(directory, path) && IsBrowsableDirectory(path))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<ContentCleanupPreview>> PreviewAsync(
        ContentCleanupFolder folder, CancellationToken cancellationToken = default)
    {
        var directory = ResolveFolder(folder);
        var files = fileSystem.EnumerateFiles(directory)
            .Where(path => IsDirectChild(directory, path) && IsSrt(path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var results = new List<ContentCleanupPreview>(files.Length);
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            try
            {
                ResolveFile(folder, name);
                var (bytes, fingerprint) = await ReadSourceAsync(path, cancellationToken);
                var result = SrtContentCleaner.Clean(bytes, cancellationToken);
                results.Add(new(name,
                    result.CleanedBytes is null ? ContentCleanupStatus.Unchanged : ContentCleanupStatus.Changed,
                    Fingerprint: fingerprint, Counts: result.Counts, Examples: result.Examples,
                    PossibleKaraokeCues: result.PossibleKaraokeCues));
            }
            catch (Exception ex) when (IsFileIssue(ex))
            {
                results.Add(new(name, ContentCleanupStatus.Skipped, ex.Message));
            }
        }
        return results;
    }

    public async Task<IReadOnlyList<ContentCleanupPreview>> ApplyAsync(
        ContentCleanupFolder folder,
        IReadOnlyList<ContentCleanupPreview> selection,
        CancellationToken cancellationToken = default)
    {
        using var lease = await gate.EnterAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await ReconcileAsync(db, cancellationToken);
        var results = new List<ContentCleanupPreview>(selection.Count);
        foreach (var file in selection)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Status != ContentCleanupStatus.Changed || file.Fingerprint is null)
            {
                continue;
            }

            string path;
            byte[] original;
            byte[]? cleaned;
            try
            {
                path = ResolveFile(folder, file.FileName);
                var source = await ReadSourceAsync(path, cancellationToken);
                if (source.Fingerprint != file.Fingerprint)
                {
                    throw new IOException("The subtitle changed after preview.");
                }
                original = source.Bytes;
                cleaned = SrtContentCleaner.Clean(original, cancellationToken).CleanedBytes;
            }
            catch (Exception ex) when (IsFileIssue(ex))
            {
                results.Add(new(file.FileName, ContentCleanupStatus.Skipped, $"{ex.Message} Run a new preview."));
                continue;
            }

            if (cleaned is null)
            {
                results.Add(new(file.FileName, ContentCleanupStatus.Unchanged));
                continue;
            }

            try
            {
                await ApplyFileAsync(db, folder, file.FileName, path, file.Fingerprint, original, cleaned, cancellationToken);
                results.Add(new(file.FileName, ContentCleanupStatus.Updated));
            }
            catch (Exception ex) when (IsFileIssue(ex) || ex is DbUpdateException)
            {
                logger.LogWarning(ex, "Could not clean subtitle {Path}.", path);
                results.Add(new(file.FileName, ContentCleanupStatus.Failed, ex.Message));
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
        return results;
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await gate.EnterAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await ReconcileAsync(db, cancellationToken);
    }

    public async Task RestoreAsync(int id, CancellationToken cancellationToken = default)
    {
        using var lease = await gate.EnterAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var operation = await db.FileOperations.SingleAsync(record =>
            record.Id == id && record.Type == OperationType.ContentCleanup, cancellationToken);
        await ReconcileOperationAsync(db, operation, cancellationToken);
        if (operation.Status != OperationStatus.Applied || operation.CleanedSha256 is null)
        {
            throw new InvalidOperationException("This cleanup operation is unavailable for restore.");
        }

        var path = ResolveRecordedSource(operation.SourcePath);
        var backup = ResolveBackup(operation.DestinationPath);
        var (original, fingerprint) = await ReadSourceAsync(backup, cancellationToken);
        if (fingerprint.Sha256 != operation.Sha256)
        {
            throw new IOException("The backup failed verification.");
        }

        var temporary = TemporaryPath(path);
        try
        {
            await fileSystem.WriteNewAsync(temporary, original, cancellationToken);
            if (await fileSystem.ComputeSha256Async(temporary, cancellationToken) != operation.Sha256)
            {
                throw new IOException("The staged subtitle failed verification.");
            }
            var current = await fileSystem.ComputeSha256Async(ResolveRecordedSource(path), cancellationToken);
            if (current != operation.CleanedSha256)
            {
                throw new IOException("The current subtitle differs from the cleaned version. The backup is still available.");
            }

            operation.Status = OperationStatus.CleanupRestoring;
            operation.Error = null;
            await db.SaveChangesAsync(cancellationToken);
            try
            {
                if (await fileSystem.ComputeSha256Async(ResolveRecordedSource(path), CancellationToken.None) !=
                    operation.CleanedSha256)
                {
                    throw new IOException("The cleaned subtitle changed while preparing the restore.");
                }
                fileSystem.Replace(temporary, ResolveRecordedSource(path));
            }
            catch (Exception ex) when (IsFileIssue(ex))
            {
                await ReconcileOperationAsync(db, operation, CancellationToken.None);
                if (operation.Status != OperationStatus.Restored)
                {
                    throw;
                }
            }
            operation.Status = OperationStatus.Restored;
            operation.OccurredUtc = clock.UtcNow;
            operation.Error = null;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        finally
        {
            DeleteTemporary(temporary);
        }
    }

    private async Task ApplyFileAsync(
        SubtitleCleanupDbContext db, ContentCleanupFolder folder, string name, string path,
        SubtitleFingerprint fingerprint, byte[] original, byte[] cleaned, CancellationToken cancellationToken)
    {
        var backup = Path.Combine(BackupDirectory, $"{Guid.NewGuid():N}.srt.bak");
        var temporary = TemporaryPath(path);
        var cleanedHash = Hash(cleaned);
        var journalAttempted = false;
        try
        {
            fileSystem.CreateDirectory(BackupDirectory);
            EnsureDirectory(BackupDirectory);
            await fileSystem.WriteNewAsync(backup, original, cancellationToken);
            if (await fileSystem.ComputeSha256Async(backup, cancellationToken) != fingerprint.Sha256)
            {
                throw new IOException("The backup failed verification.");
            }
            await fileSystem.WriteNewAsync(temporary, cleaned, cancellationToken);
            if (await fileSystem.ComputeSha256Async(temporary, cancellationToken) != cleanedHash)
            {
                throw new IOException("The staged subtitle failed verification.");
            }
            var operation = new FileOperationRecord
            {
                Type = OperationType.ContentCleanup,
                Status = OperationStatus.CleanupPending,
                SourcePath = path,
                DestinationPath = backup,
                Sha256 = fingerprint.Sha256,
                CleanedSha256 = cleanedHash,
                OccurredUtc = clock.UtcNow
            };
            db.FileOperations.Add(operation);
            journalAttempted = true;
            await db.SaveChangesAsync(cancellationToken);
            try
            {
                var current = ResolveFile(folder, name);
                var info = fileSystem.GetFileInfo(current);
                if (info.Length != fingerprint.Size || info.LastWriteTimeUtc != fingerprint.LastWriteUtc ||
                    await fileSystem.ComputeSha256Async(current, CancellationToken.None) != fingerprint.Sha256)
                {
                    throw new IOException("The subtitle changed while preparing the replacement. Run a new preview.");
                }
                fileSystem.Replace(temporary, ResolveFile(folder, name));
            }
            catch (Exception ex) when (IsFileIssue(ex))
            {
                await ReconcileOperationAsync(db, operation, CancellationToken.None, ex.Message);
                if (operation.Status != OperationStatus.Applied)
                {
                    throw;
                }
            }
            operation.Status = OperationStatus.Applied;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        finally
        {
            DeleteTemporary(temporary);
            if (!journalAttempted)
            {
                DeleteTemporary(backup);
            }
        }
    }

    private async Task ReconcileAsync(SubtitleCleanupDbContext db, CancellationToken cancellationToken)
    {
        var pending = await db.FileOperations.Where(record => record.Type == OperationType.ContentCleanup &&
            (record.Status == OperationStatus.CleanupPending || record.Status == OperationStatus.CleanupRestoring))
            .ToListAsync(cancellationToken);
        foreach (var operation in pending)
        {
            await ReconcileOperationAsync(db, operation, cancellationToken);
        }
    }

    private async Task ReconcileOperationAsync(
        SubtitleCleanupDbContext db, FileOperationRecord operation, CancellationToken cancellationToken, string? error = null)
    {
        if (operation.Status is not (OperationStatus.CleanupPending or OperationStatus.CleanupRestoring))
        {
            return;
        }

        var restoring = operation.Status == OperationStatus.CleanupRestoring;
        string? current = null;
        try
        {
            current = await fileSystem.ComputeSha256Async(ResolveRecordedSource(operation.SourcePath), cancellationToken);
        }
        catch (Exception ex) when (IsFileIssue(ex))
        {
            // A missing mount or inaccessible directory can become available later.
            operation.Error = error ?? $"Could not verify the current subtitle. {ex.Message}";
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (current == operation.CleanedSha256)
        {
            operation.Status = OperationStatus.Applied;
            operation.Error = restoring ? "Restore was interrupted before replacing the subtitle." : null;
        }
        else if (current == operation.Sha256)
        {
            operation.Status = restoring ? OperationStatus.Restored : OperationStatus.Failed;
            operation.Error = restoring ? null : error ?? "Cleanup was interrupted before replacing the subtitle.";
        }
        else
        {
            operation.Status = restoring ? OperationStatus.Applied : OperationStatus.Failed;
            operation.Error = error ?? "The current subtitle has changed. The backup is still available.";
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<(byte[] Bytes, SubtitleFingerprint Fingerprint)> ReadSourceAsync(
        string path, CancellationToken cancellationToken)
    {
        var info = fileSystem.GetFileInfo(path);
        var size = info.Length;
        var modified = info.LastWriteTimeUtc;
        if (size > SrtContentCleaner.MaxFileBytes)
        {
            throw new InvalidDataException("The subtitle exceeds the 64 MiB cleanup limit.");
        }
        var bytes = await fileSystem.ReadBytesAsync(path, SrtContentCleaner.MaxFileBytes + 1, cancellationToken);
        info.Refresh();
        if (bytes.Length != size || info.Length != size || info.LastWriteTimeUtc != modified)
        {
            throw new IOException("The subtitle changed while it was being read.");
        }
        return (bytes, new SubtitleFingerprint(size, modified, Hash(bytes)));
    }

    private string ResolveFolder(ContentCleanupFolder folder)
    {
        var root = options.Value.Roots.SingleOrDefault(root =>
            string.Equals(root.Name, folder.RootName, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("The selected media root is unavailable.");
        var rootPath = Path.GetFullPath(root.Path);
        if (Path.IsPathRooted(folder.RelativePath))
        {
            throw new IOException("The folder must be relative to the selected media root.");
        }
        var directory = rootPath;
        EnsureDirectory(directory);
        foreach (var segment in Segments(folder.RelativePath))
        {
            EnsureSegment(segment);
            var child = Path.GetFullPath(Path.Combine(directory, segment));
            if (!IsDirectChild(directory, child))
            {
                throw new IOException("The folder must stay inside the selected media root.");
            }
            EnsureDirectory(child);
            directory = child;
        }
        return directory;
    }

    private string ResolveFile(ContentCleanupFolder folder, string name)
    {
        EnsureSegment(name);
        var directory = ResolveFolder(folder);
        var path = Path.GetFullPath(Path.Combine(directory, name));
        if (!IsSrt(name) || !IsDirectChild(directory, path))
        {
            throw new IOException("Select an SRT file directly inside the chosen folder.");
        }
        EnsureRegularFile(path);
        return path;
    }

    private string ResolveRecordedSource(string path)
    {
        foreach (var root in options.Value.Roots)
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root.Path), path);
            if (!Path.IsPathRooted(relative) && relative != "." && !Segments(relative).Contains(".."))
            {
                var folder = new ContentCleanupFolder(root.Name, Path.GetDirectoryName(relative) ?? "");
                var resolved = ResolveFile(folder, Path.GetFileName(relative));
                if (string.Equals(resolved, path, PathComparison))
                {
                    return resolved;
                }
            }
        }
        throw new IOException("The original subtitle is outside the configured media roots.");
    }

    private string ResolveBackup(string path)
    {
        EnsureDirectory(BackupDirectory);
        if (!IsDirectChild(BackupDirectory, path))
        {
            throw new IOException("The backup is outside the configured backup storage.");
        }
        EnsureRegularFile(path);
        return path;
    }

    private static void EnsureSegment(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal) ||
            (OperatingSystem.IsWindows() && name.TrimEnd(' ', '.') != name))
        {
            throw new IOException("The selected path has an invalid name.");
        }
    }

    private static string[] Segments(string path) =>
        path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    private static bool IsDirectChild(string parent, string path) =>
        string.Equals(Path.TrimEndingDirectorySeparator(parent), Path.GetDirectoryName(path), PathComparison);

    private static bool IsSrt(string path) => Path.GetExtension(path).Equals(".srt", StringComparison.OrdinalIgnoreCase);

    private static void EnsureDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
        {
            throw new IOException("The folder is unavailable or is a link.");
        }
    }

    private static bool IsBrowsableDirectory(string path)
    {
        try
        {
            EnsureDirectory(path);
            return true;
        }
        catch (Exception ex) when (IsFileIssue(ex))
        {
            return false;
        }
    }

    private static void EnsureRegularFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new IOException("The subtitle is a link or is not a regular file.");
        }
    }

    private static bool IsFileIssue(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException;

    private static string TemporaryPath(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!, $".subtitlecleanup-{Guid.NewGuid():N}.tmp");

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private void DeleteTemporary(string path)
    {
        try
        {
            fileSystem.Delete(path);
        }
        catch (Exception ex) when (IsFileIssue(ex))
        {
            logger.LogWarning(ex, "Could not remove temporary cleanup file {Path}.", path);
        }
    }
}
