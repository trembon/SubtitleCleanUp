using Microsoft.Extensions.Options;
using SubtitleCleanUp.Core.Abstractions;
using SubtitleCleanUp.Core.Configuration;
using SubtitleCleanUp.Core.Services;

namespace SubtitleCleanUp.Web.Services;

public sealed class ScanScheduler(
    ScanCoordinator coordinator,
    IOptions<SubtitleCleanupOptions> options,
    ISystemClock clock,
    ILogger<ScanScheduler> logger) : BackgroundService
{
    public DateTimeOffset? NextScanUtc { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.ScanOnStartup)
        {
            await RunScanAsync(stoppingToken);
        }

        var schedule = FiveFieldCronSchedule.Parse(options.Value.ScanCron);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
        while (!stoppingToken.IsCancellationRequested)
        {
            NextScanUtc = schedule.GetNextOccurrence(clock.UtcNow, timeZone);
            while (NextScanUtc.Value > clock.UtcNow)
            {
                var remaining = NextScanUtc.Value - clock.UtcNow;
                await Task.Delay(
                    remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining,
                    stoppingToken);
            }

            await RunScanAsync(stoppingToken);
        }
    }

    private async Task RunScanAsync(CancellationToken stoppingToken)
    {
        try
        {
            await coordinator.ScanAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "A scheduled subtitle scan failed.");
        }
    }
}
