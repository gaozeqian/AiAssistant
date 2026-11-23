using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>
/// Testable clock abstraction providing UTC instant and active local timezone (ADR-0009).
/// Aligned with <see cref="TimeProvider"/> so tests can inject a fake clock or a custom TimeProvider.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    TimeZoneInfo LocalTimeZone { get; }

    DateTimeOffset LocalNow => TimeZoneInfo.ConvertTime(UtcNow, LocalTimeZone);

    // 默认实现兼容既有测试替身；需要确定性定时器时由实现方覆盖。
    TimeProvider TimeProvider => System.TimeProvider.System;

    OccurredTimestamp Now() => OccurredTimestamp.FromInstant(UtcNow, LocalTimeZone);
}

public sealed class SystemClock(TimeProvider? timeProvider = null) : IClock
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    public TimeZoneInfo LocalTimeZone => _timeProvider.LocalTimeZone;

    public TimeProvider TimeProvider => _timeProvider;
}
