using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>
/// Single-microphone hub multiplexing audio frames to KWS, VAD, and ASR without device contention (ADR-0007).
/// </summary>
public sealed record AudioSubscription(string SubscriberName, bool IncludePreRollRingBuffer = false);
