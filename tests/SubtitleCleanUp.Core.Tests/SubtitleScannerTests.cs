using NSubstitute;
using Shouldly;
using SubtitleCleanUp.Core.Abstractions;
using SubtitleCleanUp.Core.Configuration;
using SubtitleCleanUp.Core.Services;

namespace SubtitleCleanUp.Core.Tests;

public sealed class SubtitleScannerTests
{
    [Fact]
    public async Task DiscoverAsync_reports_missing_root_without_throwing()
    {
        var fileSystem = Substitute.For<ISubtitleFileSystem>();
        fileSystem.DirectoryExists("/media").Returns(false);
        var parser = Substitute.For<ISubtitleFilenameParser>();
        var scanner = new SubtitleScanner(fileSystem, parser);

        var result = await scanner.DiscoverAsync(
            [new MediaRootOptions { Name = "movies", Path = "/media" }],
            CancellationToken.None);

        result.Files.ShouldBeEmpty();
        result.Errors.ShouldHaveSingleItem().ShouldContain("unavailable");
        fileSystem.Received(1).DirectoryExists("/media");
        parser.DidNotReceiveWithAnyArgs().Parse(default!, default!);
    }

    [Fact]
    public async Task DiscoverAsync_finds_and_hashes_srt_beside_video()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "Movie.mkv"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "Movie.en.hi.srt"), "1\n00:00:01,000 --> 00:00:02,000\nHello");
        var scanner = new SubtitleScanner(
            new PhysicalSubtitleFileSystem(),
            new SubtitleFilenameParser(new IsoLanguageCatalog()));

        var result = await scanner.DiscoverAsync(
            [new MediaRootOptions { Name = "movies", Path = directory.Path }],
            CancellationToken.None);

        var file = result.Files.ShouldHaveSingleItem();
        file.Language.ShouldBe("eng");
        file.Variant.ShouldBe("sdh");
        file.CanonicalPath.ShouldEndWith("Movie.eng.sdh.srt");
        file.Fingerprint.Sha256.Length.ShouldBe(64);
        result.Errors.ShouldBeEmpty();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"subtitlecleanup-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
