using System.Security.Cryptography;
using SubtitleCleanUp.Core.Abstractions;

namespace SubtitleCleanUp.Core.Services;

public sealed class PhysicalSubtitleFileSystem : ISubtitleFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);
    public IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);
    public IEnumerable<string> EnumerateFiles(string path) => Directory.EnumerateFiles(path);
    public FileInfo GetFileInfo(string path) => new(path);

    public async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    public async Task<byte[]> ReadBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var length = (int)Math.Min(stream.Length, maximumBytes);
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        return read == buffer.Length ? buffer : buffer[..read];
    }

    public async Task CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    public void Move(string source, string destination) => File.Move(source, destination);
    public void Replace(string source, string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(source, File.GetUnixFileMode(destination));
        }
        File.Replace(source, destination, null);
    }

    public async Task WriteNewAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    public void Delete(string path) => File.Delete(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
}
