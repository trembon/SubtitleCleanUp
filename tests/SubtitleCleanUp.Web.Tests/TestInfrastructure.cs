using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SubtitleCleanUp.Web.Data;

namespace SubtitleCleanUp.Web.Tests;

internal sealed class TestDbFactory : IDbContextFactory<SubtitleCleanupDbContext>, IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<SubtitleCleanupDbContext> _options;

    public TestDbFactory()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<SubtitleCleanupDbContext>()
            .UseSqlite(_connection)
            .Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public SubtitleCleanupDbContext CreateDbContext() => new(_options);

    public ValueTask<SubtitleCleanupDbContext> CreateDbContextAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(CreateDbContext());

    public void Dispose() => _connection.Dispose();
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"subtitlecleanup-web-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public void Dispose() => Directory.Delete(Path, true);
}
