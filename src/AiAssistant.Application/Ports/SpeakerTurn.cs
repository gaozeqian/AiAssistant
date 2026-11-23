using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record SpeakerTurn(
    string SpeakerId,
    TimeSpan Start,
    TimeSpan End);
