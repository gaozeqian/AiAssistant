using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record ScreenCaptureFrame(
    OccurredTimestamp Timestamp,
    int Width,
    int Height,
    ulong PerceptualHash,
    byte[] EncodedPngBytes);
