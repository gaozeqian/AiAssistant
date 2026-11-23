using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record MediaRetentionMetrics(
    long TotalSweeps,
    long TotalPurgedBlobs,
    DateTimeOffset? LastSweepAtUtc);
