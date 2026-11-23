using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record EventWithMediaWrite(
    ActivityEvent Event,
    byte[]? RawMediaBytes = null,
    ContentKind? MediaKind = null,
    TimeSpan? MediaRetention = null);
