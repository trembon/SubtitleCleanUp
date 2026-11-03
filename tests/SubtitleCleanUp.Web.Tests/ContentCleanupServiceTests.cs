using System.Security.Cryptography;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using SubtitleCleanUp.Core.Abstractions;
using SubtitleCleanUp.Core.Configuration;
using SubtitleCleanUp.Core.Models;
using SubtitleCleanUp.Core.Services;
using SubtitleCleanUp.Web.Data;
using SubtitleCleanUp.Web.Components.Pages;
using SubtitleCleanUp.Web.Services;

namespace SubtitleCleanUp.Web.Tests;

public sealed class ContentCleanupServiceTests : IDisposable
{
    private const string Dirty = "1\n00:00:00,000 --> 00:00:01,000\nHi{note}\n\n";
    private const string Clean = "1\n00:00:00,000 --> 00:00:01,000\nHi\n\n";
    private static readonly ContentCleanupFolder Folder = new("media", "Season 2");
    private readonly TemporaryDirectory _temp = new();
    private readonly TestDbFactory _factory = new();
    private readonly PhysicalSubtitleFileSystem _physical = new();
    private readonly string _root;
    private readonly string _season;
    private readonly string _quarantine;

    public ContentCleanupServiceTests()
    {
        _root = Path.Combine(_temp.Path, "media");
        _season = Directory.CreateDirectory(Path.Combine(_root, Folder.RelativePath)).FullName;
        _quarantine = Path.Combine(_temp.Path, "quarantine");
    }

    [Fact]
    public async Task Previews_all_direct_languages_and_applies_only_selected_files_with_verified_backup_and_restore()
    {
        var cleaner = CreateCleaner();
        var english = await WriteAsync("episode.eng.srt", Dirty);
        var swedish = await WriteAsync("episode.swe.SRT", Dirty);
        var unchanged = await WriteAsync("unchanged.jpn.srt", Clean);
        var malformed = await WriteAsync("broken.srt", "not an SRT");
        var invalidEncoding = Path.Combine(_season, "encoding.srt");
        await File.WriteAllBytesAsync(invalidEncoding, [0xFF, 0xFF]);
        var largePath = Path.Combine(_season, "large.srt");
        using (var large = File.Create(largePath))
        {
            large.SetLength(SrtContentCleaner.MaxFileBytes + 1);
        }
        Directory.CreateDirectory(Path.Combine(_season, "nested"));
        await WriteAsync("nested/episode.srt", Dirty);
        await WriteAsync("video.mkv", "movie");
        var original = await File.ReadAllBytesAsync(english);
        var timestamp = File.GetLastWriteTimeUtc(english);

        cleaner.ListFolders(new("media", "")).ShouldBe(["Season 2"]);
        var rows = await cleaner.PreviewAsync(Folder);
        rows.Count.ShouldBe(6);
        rows.Count(row => row.Status == ContentCleanupStatus.Changed).ShouldBe(2);
        rows.Single(row => row.FileName == Path.GetFileName(unchanged)).Status.ShouldBe(ContentCleanupStatus.Unchanged);
        rows.Single(row => row.FileName == Path.GetFileName(malformed)).Status.ShouldBe(ContentCleanupStatus.Skipped);
        rows.Count(row => row.Status == ContentCleanupStatus.Skipped).ShouldBe(3);
        File.GetLastWriteTimeUtc(english).ShouldBe(timestamp);
        (await File.ReadAllBytesAsync(english)).ShouldBe(original);
        Directory.Exists(_quarantine).ShouldBeFalse();
        using var db = _factory.CreateDbContext();
        (await db.FileOperations.CountAsync()).ShouldBe(0);

        var results = await cleaner.ApplyAsync(Folder, [rows.Single(row => row.FileName == Path.GetFileName(english))]);
        results.Single().Status.ShouldBe(ContentCleanupStatus.Updated);
        (await File.ReadAllTextAsync(english)).ShouldBe(Clean);
        (await File.ReadAllTextAsync(swedish)).ShouldBe(Dirty);
        var operation = await db.FileOperations.AsNoTracking().SingleAsync();
        operation.Type.ShouldBe(OperationType.ContentCleanup);
        operation.Status.ShouldBe(OperationStatus.Applied);
        (await File.ReadAllBytesAsync(operation.DestinationPath)).ShouldBe(original);

        await cleaner.RestoreAsync(operation.Id);
        (await File.ReadAllBytesAsync(english)).ShouldBe(original);
        (await db.FileOperations.AsNoTracking().SingleAsync()).Status.ShouldBe(OperationStatus.Restored);
    }

    [Fact]
    public async Task Page_previews_and_waits_for_confirmation_before_applying_the_selected_file()
    {
        var english = await WriteAsync("episode.eng.srt", Dirty);
        var swedish = await WriteAsync("episode.swe.srt", Dirty);
        Directory.CreateDirectory(Path.Combine(_season, "Extras"));
        using var context = new BunitContext();
        context.Services.AddSingleton(CreateCleaner());
        var page = context.Render<ContentCleanup>();

        Button("Season 2").Click();
        Button("Extras").Click();
        page.Find(".cleanup-path code").TextContent.ShouldBe("media/Season 2/Extras");
        Button("↑ Parent folder").Click();
        Button("Preview folder").Click();
        page.WaitForAssertion(() => page.FindAll("input[type=checkbox][checked]").Count.ShouldBe(2));
        page.FindAll("input[type=checkbox]")[1].Change(false);
        Button("Apply selected (1)").Click();
        page.Find("[role=dialog]").TextContent.ShouldContain("Clean 1 selected files?");
        (await File.ReadAllTextAsync(english)).ShouldBe(Dirty);
        (await File.ReadAllTextAsync(swedish)).ShouldBe(Dirty);

        Button("Confirm cleanup").Click();
        page.WaitForAssertion(() => page.Markup.ShouldContain("1 updated"));
        page.Markup.ShouldContain("1 skipped");
        (await File.ReadAllTextAsync(english)).ShouldBe(Clean);
        (await File.ReadAllTextAsync(swedish)).ShouldBe(Dirty);

        AngleSharp.Dom.IElement Button(string label) =>
            page.FindAll("button").Single(button => button.TextContent.Trim() == label);
    }

    [Fact]
    public async Task Refuses_stale_preview_and_changed_restore_target()
    {
        var cleaner = CreateCleaner();
        var path = await WriteAsync("episode.srt", Dirty);
        var rows = await cleaner.PreviewAsync(Folder);
        await File.WriteAllTextAsync(path, Dirty.Replace("Hi", "Oh"));

        (await cleaner.ApplyAsync(Folder, rows)).Single().Status.ShouldBe(ContentCleanupStatus.Skipped);
        var fresh = await cleaner.PreviewAsync(Folder);
        (await cleaner.ApplyAsync(Folder, fresh)).Single().Status.ShouldBe(ContentCleanupStatus.Updated);
        await File.WriteAllTextAsync(path, "new media server subtitle");
        using var db = _factory.CreateDbContext();
        var operation = await db.FileOperations.SingleAsync();

        await Should.ThrowAsync<IOException>(() => cleaner.RestoreAsync(operation.Id));
        (await File.ReadAllTextAsync(path)).ShouldBe("new media server subtitle");
        (await File.ReadAllTextAsync(operation.DestinationPath)).ShouldBe(Dirty.Replace("Hi", "Oh"));
    }

    [Fact]
    public async Task Rejects_root_escape_and_skips_links()
    {
        var cleaner = CreateCleaner();
        var outside = Directory.CreateDirectory(Path.Combine(_temp.Path, "outside")).FullName;
        var target = Path.Combine(outside, "external.srt");
        await File.WriteAllTextAsync(target, Dirty);
        await Should.ThrowAsync<IOException>(() => cleaner.PreviewAsync(new("media", "../outside")));
        await Should.ThrowAsync<IOException>(() => cleaner.PreviewAsync(new("media", outside)));

        // Windows runners may lack the privilege to create a symbolic link.
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_season, "linked-folder"), outside);
            File.CreateSymbolicLink(Path.Combine(_season, "linked.srt"), target);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            return;
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            return;
        }
        cleaner.ListFolders(Folder).ShouldBeEmpty();
        await Should.ThrowAsync<IOException>(() => cleaner.PreviewAsync(new("media", "Season 2/linked-folder")));
        (await cleaner.PreviewAsync(Folder)).Single().Status.ShouldBe(ContentCleanupStatus.Skipped);
        (await File.ReadAllTextAsync(target)).ShouldBe(Dirty);
    }

    [Fact]
    public async Task Continues_after_failed_replacement_and_keeps_verified_original()
    {
        var fail = await WriteAsync("first.srt", Dirty);
        var next = await WriteAsync("second.srt", Dirty);
        var fileSystem = Substitute.For<ISubtitleFileSystem>();
        fileSystem.EnumerateFiles(Arg.Any<string>()).Returns(call => _physical.EnumerateFiles(call.Arg<string>()));
        fileSystem.GetFileInfo(Arg.Any<string>()).Returns(call => _physical.GetFileInfo(call.Arg<string>()));
        fileSystem.ReadBytesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call =>
            _physical.ReadBytesAsync(call.ArgAt<string>(0), call.ArgAt<int>(1), call.ArgAt<CancellationToken>(2)));
        fileSystem.ComputeSha256Async(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            _physical.ComputeSha256Async(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        fileSystem.WriteNewAsync(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(call =>
            _physical.WriteNewAsync(call.ArgAt<string>(0), call.ArgAt<ReadOnlyMemory<byte>>(1), call.ArgAt<CancellationToken>(2)));
        fileSystem.When(fs => fs.CreateDirectory(Arg.Any<string>())).Do(call => _physical.CreateDirectory(call.Arg<string>()));
        fileSystem.When(fs => fs.Delete(Arg.Any<string>())).Do(call => _physical.Delete(call.Arg<string>()));
        fileSystem.When(fs => fs.Replace(Arg.Any<string>(), fail)).Do(_ => throw new IOException("Replacement denied."));
        fileSystem.When(fs => fs.Replace(Arg.Any<string>(), next)).Do(call => _physical.Replace(call.ArgAt<string>(0), next));
        var cleaner = CreateCleaner(fileSystem);

        var results = await cleaner.ApplyAsync(Folder, await cleaner.PreviewAsync(Folder));
        results.Select(row => row.Status).ShouldBe([ContentCleanupStatus.Failed, ContentCleanupStatus.Updated]);
        (await File.ReadAllTextAsync(fail)).ShouldBe(Dirty);
        (await File.ReadAllTextAsync(next)).ShouldBe(Clean);
        Directory.GetFiles(_season, "*.tmp").ShouldBeEmpty();
        using var db = _factory.CreateDbContext();
        var failed = await db.FileOperations.SingleAsync(operation => operation.SourcePath == fail);
        failed.Status.ShouldBe(OperationStatus.Failed);
        (await File.ReadAllTextAsync(failed.DestinationPath)).ShouldBe(Dirty);
    }

    [Theory]
    [InlineData(OperationStatus.CleanupPending, Dirty, OperationStatus.Failed)]
    [InlineData(OperationStatus.CleanupPending, Clean, OperationStatus.Applied)]
    [InlineData(OperationStatus.CleanupRestoring, Clean, OperationStatus.Applied)]
    [InlineData(OperationStatus.CleanupRestoring, Dirty, OperationStatus.Restored)]
    public async Task Reconciles_interrupted_journal_without_touching_subtitle(
        OperationStatus pending, string current, OperationStatus expected)
    {
        var path = await WriteAsync("episode.srt", current);
        var backup = Path.Combine(Directory.CreateDirectory(Path.Combine(_quarantine, "content-cleanup")).FullName, "original.srt.bak");
        await File.WriteAllTextAsync(backup, Dirty);
        using (var db = _factory.CreateDbContext())
        {
            db.FileOperations.Add(new()
            {
                Type = OperationType.ContentCleanup,
                Status = pending,
                SourcePath = path,
                DestinationPath = backup,
                Sha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Dirty))),
                CleanedSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Clean)))
            });
            await db.SaveChangesAsync();
        }

        await CreateCleaner().ReconcileAsync();

        using var verify = _factory.CreateDbContext();
        (await verify.FileOperations.SingleAsync()).Status.ShouldBe(expected);
        (await File.ReadAllTextAsync(path)).ShouldBe(current);
        (await File.ReadAllTextAsync(backup)).ShouldBe(Dirty);
    }

    private ContentCleanupService CreateCleaner(ISubtitleFileSystem? fileSystem = null) => new(
        _factory, fileSystem ?? _physical,
        Options.Create(new SubtitleCleanupOptions
        {
            Roots = [new() { Name = "media", Path = _root }],
            QuarantineRoot = _quarantine
        }),
        new OperationGate(), new SystemClock(), NullLogger<ContentCleanupService>.Instance);

    private async Task<string> WriteAsync(string file, string text)
    {
        var path = Path.Combine(_season, file);
        await File.WriteAllTextAsync(path, text);
        return path;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _temp.Dispose();
    }
}
