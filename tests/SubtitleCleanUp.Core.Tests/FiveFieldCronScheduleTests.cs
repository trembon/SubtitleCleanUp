using Shouldly;
using SubtitleCleanUp.Core.Services;

namespace SubtitleCleanUp.Core.Tests;

public sealed class FiveFieldCronScheduleTests
{
    [Fact]
    public void GetNextOccurrence_honors_time_zone()
    {
        var schedule = FiveFieldCronSchedule.Parse("0 3 * * *");
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

        var next = schedule.GetNextOccurrence(
            new DateTimeOffset(2026, 7, 24, 0, 30, 0, TimeSpan.Zero),
            zone);

        next.ShouldBe(new DateTimeOffset(2026, 7, 24, 1, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Parse_supports_lists_ranges_and_steps()
    {
        var schedule = FiveFieldCronSchedule.Parse("*/15 8-9 * * 1,3");
        var zone = TimeZoneInfo.Utc;

        var next = schedule.GetNextOccurrence(
            new DateTimeOffset(2026, 7, 27, 7, 59, 0, TimeSpan.Zero),
            zone);

        next.ShouldBe(new DateTimeOffset(2026, 7, 27, 8, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("* * * *")]
    [InlineData("60 * * * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("* * 31-2 * *")]
    public void Parse_rejects_invalid_expressions(string expression)
    {
        Should.Throw<FormatException>(() => FiveFieldCronSchedule.Parse(expression));
    }
}
