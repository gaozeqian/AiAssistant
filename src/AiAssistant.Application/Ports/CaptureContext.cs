using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record CaptureContext(
    IReadOnlyList<string> WhitelistedDirectories,
    TimeSpan FallbackInterval,
    int MaxTextFileBytes = 200 * 1024);
