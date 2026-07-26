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
using SubtitleCleanUp.Web.Components.Pages;
using SubtitleCleanUp.Web.Services;

namespace SubtitleCleanUp.Web.Tests;

public sealed class HomeComponentTests
{
    [Fact]
    public async Task Scan_button_reenables_when_background_operation_finishes()
    {
        using var factory = new TestDbFactory();
        using var context = new BunitContext();
        var gate = new OperationGate();
        var lease = await gate.EnterAsync(CancellationToken.None);
        var options = Options.Create(new SubtitleCleanupOptions
        {
            Roots = [new MediaRootOptions { Name = "media", Path = "/media" }]
        });
        var scanner = Substitute.For<ISubtitleScanner>();
        scanner.DiscoverAsync(
                Arg.Any<IReadOnlyCollection<MediaRootOptions>>(),
                Arg.Any<CancellationToken>())
            .Returns(new SubtitleDiscovery([], []));
        var planner = Substitute.For<IChangePlanner>();
        planner.CreatePlan(Arg.Any<SubtitleDiscovery>()).Returns([]);
        var clock = Substitute.For<ISystemClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var coordinator = new ScanCoordinator(
            factory,
            scanner,
            planner,
            options,
            clock,
            gate,
            NullLogger<ScanCoordinator>.Instance);
        var scheduler = new ScanScheduler(
            coordinator,
            options,
            clock,
            NullLogger<ScanScheduler>.Instance);

        context.Services.AddSingleton<IDbContextFactory<Data.SubtitleCleanupDbContext>>(factory);
        context.Services.AddSingleton(coordinator);
        context.Services.AddSingleton(scheduler);
        context.Services.AddSingleton(gate);
        context.Services.AddSingleton<IOptions<SubtitleCleanupOptions>>(options);

        var component = context.Render<Home>();

        component.Find("button").HasAttribute("disabled").ShouldBeTrue();

        lease.Dispose();

        component.WaitForAssertion(
            () => component.Find("button").HasAttribute("disabled").ShouldBeFalse());
    }
}
