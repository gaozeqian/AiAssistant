using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record VadSegment(
    TimeSpan Start,
    TimeSpan End,
    ReadOnlyMemory<float> Samples);
