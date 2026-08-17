using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using SubtitleCleanUp.Core.Abstractions;
using SubtitleCleanUp.Core.Configuration;
using SubtitleCleanUp.Core.Services;
using SubtitleCleanUp.Web.Components.Pages;
using SubtitleCleanUp.Web.Services;

namespace SubtitleCleanUp.Web.Tests;

public sealed class ReviewComponentTests
{
    [Fact]
    public void Review_shows_empty_state_when_no_proposals_exist()
    {
        using var factory = new TestDbFactory();
        using var context = new BunitContext();
        context.Services.AddSingleton<IDbContextFactory<Data.SubtitleCleanupDbContext>>(factory);
        context.Services.AddSingleton(new ChangeExecutionService(
            factory,
            new PhysicalSubtitleFileSystem(),
            Options.Create(new SubtitleCleanupOptions { QuarantineRoot = Path.GetTempPath() }),
            new SystemClock(),
            new OperationGate(),
            NullLogger<ChangeExecutionService>.Instance));
        context.Services.AddSingleton(new SubtitlePreviewService(
            factory,
            new PhysicalSubtitleFileSystem(),
            Options.Create(new SubtitleCleanupOptions())));

        var component = context.Render<Review>();

        component.Markup.ShouldContain("Nothing matches this view");
        component.Markup.ShouldContain("Review queue");
    }

    [Fact]
    public void Review_selects_and_persists_the_recommended_file_when_keeper_is_missing()
    {
        using var factory = new TestDbFactory();
        using (var db = factory.CreateDbContext())
        {
            var proposal = new Data.ChangeProposal
            {
                GroupKey = "group",
                FingerprintSignature = "fingerprint",
                RootName = "media",
                DirectoryPath = "/media",
                MediaStem = "Movie",
                Kind = Core.Models.ProposalKind.Duplicate,
                Reason = "duplicate",
                Files =
                [
                    new Data.SubtitleFileRecord { FileName = "Movie.en.srt", RelativePath = "Movie.en.srt", FullPath = "/media/Movie.en.srt", RootName = "media", RootPath = "/media", IsRecommended = false },
                    new Data.SubtitleFileRecord { FileName = "Movie.eng.srt", RelativePath = "Movie.eng.srt", FullPath = "/media/Movie.eng.srt", RootName = "media", RootPath = "/media", IsRecommended = true }
                ]
            };
            db.ChangeProposals.Add(proposal);
            db.SaveChanges();
        }

        using var context = new BunitContext();
        context.Services.AddSingleton<IDbContextFactory<Data.SubtitleCleanupDbContext>>(factory);
        context.Services.AddSingleton(new ChangeExecutionService(
            factory,
            new PhysicalSubtitleFileSystem(),
            Options.Create(new SubtitleCleanupOptions { QuarantineRoot = Path.GetTempPath() }),
            new SystemClock(),
            new OperationGate(),
            NullLogger<ChangeExecutionService>.Instance));
        context.Services.AddSingleton(new SubtitlePreviewService(
            factory,
            new PhysicalSubtitleFileSystem(),
            Options.Create(new SubtitleCleanupOptions())));

        var component = context.Render<Review>();

        component.FindAll("input[type=radio][checked]").Count.ShouldBe(1);
        component.Find("label.file-selection").TextContent.ShouldContain("Movie.eng.srt");
        using var verifyDb = factory.CreateDbContext();
        var stored = verifyDb.ChangeProposals.Include(x => x.Files).Single();
        stored.SelectedKeeperId.ShouldBe(stored.Files.Single(x => x.IsRecommended).Id);
    }
}
