using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record VoiceInteraction(
    VoiceSessionState State,
    string WakeWord,
    string Transcript,
    string Feedback,
    DateTimeOffset OccurredAtUtc);
