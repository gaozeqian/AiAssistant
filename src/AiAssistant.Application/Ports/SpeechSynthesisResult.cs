using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record SpeechSynthesisResult(
    ReadOnlyMemory<float> Samples,
    int SampleRateHz,
    string ModelVersion);
