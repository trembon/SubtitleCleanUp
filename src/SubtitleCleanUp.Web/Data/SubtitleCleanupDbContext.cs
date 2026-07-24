using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SubtitleCleanUp.Web.Data;

public sealed class SubtitleCleanupDbContext(DbContextOptions<SubtitleCleanupDbContext> options)
    : DbContext(options)
{
    public DbSet<ScanRun> ScanRuns => Set<ScanRun>();
    public DbSet<ScanIssue> ScanIssues => Set<ScanIssue>();
    public DbSet<ChangeProposal> ChangeProposals => Set<ChangeProposal>();
    public DbSet<SubtitleFileRecord> SubtitleFiles => Set<SubtitleFileRecord>();
    public DbSet<FileOperationRecord> FileOperations => Set<FileOperationRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChangeProposal>(entity =>
        {
            entity.HasIndex(x => new { x.GroupKey, x.Status });
            entity.Property(x => x.GroupKey).HasMaxLength(1400);
            entity.Property(x => x.FingerprintSignature).HasMaxLength(8000);
            entity.Property(x => x.Status).HasConversion<string>();
            entity.Property(x => x.Kind).HasConversion<string>();
            entity.Property(x => x.CreatedUtc)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            entity.Property(x => x.LastSeenUtc)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            entity.HasMany(x => x.Files)
                .WithOne(x => x.Proposal)
                .HasForeignKey(x => x.ChangeProposalId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.Operations)
                .WithOne(x => x.Proposal)
                .HasForeignKey(x => x.ChangeProposalId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<FileOperationRecord>(entity =>
        {
            entity.Property(x => x.Type).HasConversion<string>();
            entity.Property(x => x.Status).HasConversion<string>();
            entity.Property(x => x.OccurredUtc)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            entity.HasIndex(x => new { x.Status, x.Type });
        });

        modelBuilder.Entity<ScanRun>(entity =>
        {
            entity.Property(x => x.StartedUtc)
                .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
            entity.Property(x => x.CompletedUtc)
                .HasConversion(new ValueConverter<DateTimeOffset?, long?>(
                    value => value.HasValue ? value.Value.UtcTicks : null,
                    value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null));
            entity.HasMany(x => x.Issues)
                .WithOne(x => x.ScanRun)
                .HasForeignKey(x => x.ScanRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
