using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record AudioFrame(
    DateTimeOffset TimestampUtc,
    int SampleRateHz,
    int Channels,
    ReadOnlyMemory<float> Samples);
