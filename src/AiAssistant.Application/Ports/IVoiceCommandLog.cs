using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IVoiceCommandLog
{
    Task AppendAsync(VoiceCommandLogRecord record, CancellationToken ct);

    /// <summary>Newest first; used by the voice page to review and replay recorded commands.</summary>
    Task<IReadOnlyList<VoiceCommandLogRecord>> ListRecentAsync(int limit, CancellationToken ct);
}
