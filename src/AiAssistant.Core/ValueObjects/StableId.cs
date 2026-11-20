using System.Security.Cryptography;

namespace AiAssistant.Core.ValueObjects;

/// <summary>
/// Generates lexicographically time-sortable, collision-resistant identifiers without external dependencies.
/// </summary>
public static class StableId
{
    public static string NewEventId(DateTimeOffset utcTimestamp) => Create("evt", utcTimestamp);

    public static string NewContentId(DateTimeOffset utcTimestamp) => Create("cnt", utcTimestamp);

    public static string NewJobId(DateTimeOffset utcTimestamp) => Create("job", utcTimestamp);

    public static string NewReportId(DateTimeOffset utcTimestamp) => Create("rpt", utcTimestamp);

    public static string Create(string prefix, DateTimeOffset utcTimestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var unixMs = utcTimestamp.ToUniversalTime().ToUnixTimeMilliseconds();
        Span<byte> random = stackalloc byte[8];
        RandomNumberGenerator.Fill(random);
        return $"{prefix}_{unixMs:x12}_{Convert.ToHexStringLower(random)}";
    }
}
