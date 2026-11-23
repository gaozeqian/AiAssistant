using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record AudioTranscriptionRequest(
    ReadOnlyMemory<float> Samples,
    int SampleRateHz,
    string Language = "zh");
