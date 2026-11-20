using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>
/// Testable clock abstraction providing UTC instant and active local timezone (ADR-0009).
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
    TimeZoneInfo LocalTimeZone { get; }

    OccurredTimestamp Now() => OccurredTimestamp.FromInstant(UtcNow, LocalTimeZone);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
}
