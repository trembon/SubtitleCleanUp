using System.Text;
using Shouldly;
using SubtitleCleanUp.Core.Services;

namespace SubtitleCleanUp.Core.Tests;

public sealed class PhysicalSubtitleFileSystemTests
{
    [Fact]
    public async Task Staged_replacement_preserves_the_existing_path_and_unix_permissions()
    {
        var files = new PhysicalSubtitleFileSystem();
        var directory = Path.Combine(Path.GetTempPath(), $"subtitlecleanup-{Guid.NewGuid():N}");
        files.CreateDirectory(directory);
        var subtitle = Path.Combine(directory, "episode.srt");
        var staged = Path.Combine(directory, "episode.tmp");
        try
        {
            var original = Encoding.UTF8.GetBytes("Original");
            var cleaned = Encoding.UTF8.GetBytes("Cleaned");
            await files.WriteNewAsync(subtitle, original, CancellationToken.None);
            await files.WriteNewAsync(staged, cleaned, CancellationToken.None);
            await Should.ThrowAsync<IOException>(() => files.WriteNewAsync(staged, original, CancellationToken.None));
            (await files.ReadBytesAsync(staged, 3, CancellationToken.None)).ShouldBe(cleaned[..3]);
            (await files.ReadBytesAsync(subtitle, 20, CancellationToken.None)).ShouldBe(original);
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(staged).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(subtitle, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            }

            files.Replace(staged, subtitle);

            files.FileExists(staged).ShouldBeFalse();
            (await files.ReadBytesAsync(subtitle, 20, CancellationToken.None)).ShouldBe(cleaned);
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(subtitle).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            }
        }
        finally
        {
            files.Delete(staged);
            files.Delete(subtitle);
            Directory.Delete(directory);
        }
    }
}
