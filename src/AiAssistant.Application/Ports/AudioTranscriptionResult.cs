using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record AudioTranscriptionResult(
    string Text,
    double Confidence,
    string ModelVersion)
{
    public static readonly AudioTranscriptionResult Empty = new(string.Empty, 0, "unavailable");
}
