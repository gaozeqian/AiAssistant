namespace AiAssistant.Core.ValueObjects;

/// <summary>
/// Stores UTC instant alongside the local calendar date and timezone at capture time (ADR-0009).
/// Historical local_date is never rewritten if the user later travels to another timezone.
/// </summary>
public sealed record OccurredTimestamp
{
    public DateTimeOffset OccurredAtUtc { get; }
    public DateOnly LocalDate { get; }
    public string TimeZoneId { get; }
    public int UtcOffsetMinutes { get; }

    private OccurredTimestamp(
        DateTimeOffset occurredAtUtc,
        DateOnly localDate,
        string timeZoneId,
        int utcOffsetMinutes)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new ArgumentException("TimeZoneId is required.", nameof(timeZoneId));
        }

        if (utcOffsetMinutes is < -14 * 60 or > 14 * 60)
        {
            throw new ArgumentOutOfRangeException(nameof(utcOffsetMinutes), "UTC offset must be within [-840, +840] minutes.");
        }

        OccurredAtUtc = occurredAtUtc.ToUniversalTime();
        LocalDate = localDate;
        TimeZoneId = timeZoneId;
        UtcOffsetMinutes = utcOffsetMinutes;
    }

    public static OccurredTimestamp FromInstant(DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var utc = instant.ToUniversalTime();
        var local = TimeZoneInfo.ConvertTime(utc, timeZone);
        var offsetMinutes = (int)Math.Round(local.Offset.TotalMinutes);
        return new OccurredTimestamp(
            utc,
            DateOnly.FromDateTime(local.DateTime),
            timeZone.Id,
            offsetMinutes);
    }

    public static OccurredTimestamp FromStored(
        DateTimeOffset occurredAtUtc,
        DateOnly localDate,
        string timeZoneId,
        int utcOffsetMinutes) =>
        new(occurredAtUtc, localDate, timeZoneId, utcOffsetMinutes);

    public DateTimeOffset ToCapturedLocalTime() =>
        OccurredAtUtc.ToOffset(TimeSpan.FromMinutes(UtcOffsetMinutes));
}
