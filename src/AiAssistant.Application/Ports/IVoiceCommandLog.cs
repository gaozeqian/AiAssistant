using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IVoiceCommandLog
{
    Task AppendAsync(VoiceCommandLogRecord record, CancellationToken ct);
}
