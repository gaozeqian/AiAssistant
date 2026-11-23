using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record CapturedSample(
    OccurredTimestamp Timestamp,
    SourceKind Source,
    string AppName,
    string WindowTitle,
    string? ExtractedText = null,
    byte[]? RawMediaBytes = null,
    ContentKind? MediaKind = null,
    ulong? PerceptualHash = null,
    nint NativeWindowHandle = 0,
    IReadOnlyDictionary<string, string>? Metadata = null);
