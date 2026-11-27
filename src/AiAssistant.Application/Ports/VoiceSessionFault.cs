namespace AiAssistant.Application.Ports;

/// <summary>
/// Reported when the voice session stops without the user asking it to. The UI must turn the
/// enable switch off and tell the user to re-enable voice; silently staying "armed" would leave
/// the microphone closed while the page still claims it is listening.
/// </summary>
public sealed record VoiceSessionFault(string Reason, DateTimeOffset OccurredAtUtc);
