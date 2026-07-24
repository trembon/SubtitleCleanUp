using Microsoft.Extensions.Options;
using Shouldly;
using SubtitleCleanUp.Core.Configuration;
using SubtitleCleanUp.Core.Services;
using SubtitleCleanUp.Web.Data;
using SubtitleCleanUp.Web.Services;

namespace SubtitleCleanUp.Web.Tests;

public sealed class SubtitlePreviewServiceTests
{
    [Fact]
    public async Task ReadAsync_returns_utf8_text_and_reports_truncation()
    {
        using var directory = new TemporaryDirectory();
        using var factory = new TestDbFactory();
        var path = Path.Combine(directory.Path, "Movie.eng.srt");
        await File.WriteAllTextAsync(path, "1234567890");
        var id = await SeedFileAsync(factory, path);
        var service = new SubtitlePreviewService(
            factory,
            new PhysicalSubtitleFileSystem(),
            Options.Create(new SubtitleCleanupOptions { PreviewMaxBytes = 5 }));

        var result = await service.ReadAsync(id);

        result.Text.ShouldBe("1234567890");
        result.EncodingName.ShouldBe("utf-8");
        result.IsTruncated.ShouldBeFalse();
    }

    [Fact]
    public async Task ReadAsync_falls_back_to_windows_1252()
    {
        using var directory = new TemporaryDirectory();
        using var factory = new TestDbFactory();
        var path = Path.Combine(directory.Path, "Movie.eng.srt");
        await File.WriteAllBytesAsync(path, [0x43, 0x61, 0x66, 0xE9]);
        var id = await SeedFileAsync(factory, path);
        var service = new SubtitlePreviewService(
            factory,
            new PhysicalSubtitleFileSystem(),
            Options.Create(new SubtitleCleanupOptions()));

        var result = await service.ReadAsync(id);

        result.Text.ShouldBe("Café");
        result.EncodingName.ShouldBe("windows-1252");
    }

    private static async Task<int> SeedFileAsync(TestDbFactory factory, string path)
    {
        await using var db = factory.CreateDbContext();
        var proposal = new ChangeProposal
        {
            GroupKey = "group",
            FingerprintSignature = "signature",
            RootName = "media",
            DirectoryPath = Path.GetDirectoryName(path)!,
            MediaStem = "Movie",
            Language = "eng",
            Kind = SubtitleCleanUp.Core.Models.ProposalKind.Rename,
            Reason = "rename",
            CreatedUtc = DateTimeOffset.UtcNow,
            LastSeenUtc = DateTimeOffset.UtcNow,
            Files =
            [
                new SubtitleFileRecord
                {
                    RootName = "media",
                    RootPath = Path.GetDirectoryName(path)!,
                    FullPath = path,
                    RelativePath = Path.GetFileName(path),
                    FileName = Path.GetFileName(path),
                    Sha256 = "hash"
                }
            ]
        };
        db.Add(proposal);
        await db.SaveChangesAsync();
        return proposal.Files[0].Id;
    }
}
