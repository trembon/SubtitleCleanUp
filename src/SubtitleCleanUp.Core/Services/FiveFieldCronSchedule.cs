namespace SubtitleCleanUp.Core.Services;

public sealed class FiveFieldCronSchedule
{
    private readonly HashSet<int> _minutes;
    private readonly HashSet<int> _hours;
    private readonly HashSet<int> _days;
    private readonly HashSet<int> _months;
    private readonly HashSet<int> _weekdays;
    private readonly bool _dayWildcard;
    private readonly bool _weekdayWildcard;

    private FiveFieldCronSchedule(string expression)
    {
        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 5)
        {
            throw new FormatException("A five-field cron expression is required: minute hour day month weekday.");
        }

        _minutes = ParseField(parts[0], 0, 59);
        _hours = ParseField(parts[1], 0, 23);
        _days = ParseField(parts[2], 1, 31);
        _months = ParseField(parts[3], 1, 12);
        _weekdays = ParseField(parts[4], 0, 7).Select(x => x == 7 ? 0 : x).ToHashSet();
        _dayWildcard = parts[2] == "*";
        _weekdayWildcard = parts[4] == "*";
    }

    public static FiveFieldCronSchedule Parse(string expression) => new(expression);

    public DateTimeOffset GetNextOccurrence(DateTimeOffset afterUtc, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(afterUtc, timeZone);
        var candidate = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0)
            .AddMinutes(1);
        var limit = candidate.AddYears(2);

        while (candidate <= limit)
        {
            var dayMatches = _days.Contains(candidate.Day);
            var weekdayMatches = _weekdays.Contains((int)candidate.DayOfWeek);
            var calendarMatches = _dayWildcard
                ? weekdayMatches
                : _weekdayWildcard
                    ? dayMatches
                    : dayMatches || weekdayMatches;

            if (_minutes.Contains(candidate.Minute) &&
                _hours.Contains(candidate.Hour) &&
                _months.Contains(candidate.Month) &&
                calendarMatches &&
                !timeZone.IsInvalidTime(candidate))
            {
                var offset = timeZone.GetUtcOffset(candidate);
                return new DateTimeOffset(candidate, offset).ToUniversalTime();
            }

            candidate = candidate.AddMinutes(1);
        }

        throw new InvalidOperationException("The cron expression has no occurrence within two years.");
    }

    private static HashSet<int> ParseField(string value, int minimum, int maximum)
    {
        var result = new HashSet<int>();
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries))
        {
            var rangeAndStep = part.Split('/', StringSplitOptions.TrimEntries);
            if (rangeAndStep.Length > 2)
            {
                throw new FormatException($"Invalid cron field '{value}'.");
            }

            var step = rangeAndStep.Length == 2 && int.TryParse(rangeAndStep[1], out var parsedStep)
                ? parsedStep
                : 1;
            if (step <= 0)
            {
                throw new FormatException("Cron steps must be positive.");
            }

            var range = rangeAndStep[0];
            int start;
            int end;
            if (range == "*")
            {
                start = minimum;
                end = maximum;
            }
            else if (range.Contains('-'))
            {
                var bounds = range.Split('-', StringSplitOptions.TrimEntries);
                if (bounds.Length != 2 || !int.TryParse(bounds[0], out start) || !int.TryParse(bounds[1], out end))
                {
                    throw new FormatException($"Invalid cron range '{range}'.");
                }
            }
            else if (int.TryParse(range, out var number))
            {
                start = end = number;
            }
            else
            {
                throw new FormatException($"Invalid cron value '{range}'.");
            }

            if (start < minimum || end > maximum || start > end)
            {
                throw new FormatException($"Cron value '{range}' is outside {minimum}-{maximum}.");
            }

            for (var current = start; current <= end; current += step)
            {
                result.Add(current);
            }
        }

        return result;
    }
}
