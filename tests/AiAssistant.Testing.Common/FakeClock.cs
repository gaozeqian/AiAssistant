using AiAssistant.Application.Ports;

namespace AiAssistant.Testing.Common;

/// <summary>Deterministic clock with explicit advancement; never reads the system clock.</summary>
public sealed class FakeClock : IClock
{
    private readonly Lock _sync = new();
    private DateTimeOffset _utcNow;

    public FakeClock(DateTimeOffset? utcNow = null, TimeZoneInfo? localTimeZone = null)
    {
        _utcNow = utcNow ?? new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
        LocalTimeZone = localTimeZone ?? TimeZoneInfo.Utc;
    }

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_sync)
            {
                return _utcNow;
            }
        }
    }

    public TimeZoneInfo LocalTimeZone { get; }

    public void Advance(TimeSpan delta)
    {
        lock (_sync)
        {
            _utcNow += delta;
        }
    }

    public void SetUtcNow(DateTimeOffset value)
    {
        lock (_sync)
        {
            _utcNow = value;
        }
    }
}
