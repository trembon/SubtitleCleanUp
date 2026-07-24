using SubtitleCleanUp.Core.Abstractions;

namespace SubtitleCleanUp.Core.Services;

public sealed class SystemClock : ISystemClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
