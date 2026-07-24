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
}
