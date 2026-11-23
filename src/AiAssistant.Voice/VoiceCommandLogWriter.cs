using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Voice;

/// <summary>Persists one parsed voice command; logging failure must not break the response path.</summary>
internal sealed class VoiceCommandLogWriter(
    IVoiceCommandLog commandLog,
    IClock clock,
    ILogger<VoiceSessionController> logger)
{
    public async Task WriteAsync(
        string wakeKeyword,
        string transcript,
        ParsedVoiceCommand? command,
        string? audioContentId)
    {
        try
        {
            await commandLog.AppendAsync(
                new VoiceCommandLogRecord(
                    StableId.NewEventId(clock.UtcNow),
                    clock.UtcNow,
                    wakeKeyword,
                    transcript,
                    command?.Kind.ToString() ?? VoiceCommandKind.Unknown.ToString(),
                    command?.ParamsJson ?? "{}",
                    ModelEndpointTier.LocalOffline,
                    ResponseRef: null,
                    AudioContentId: audioContentId),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Voice command log failed with {ExceptionType}.", ex.GetType().Name);
        }
    }
}
