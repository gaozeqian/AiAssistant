using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record DataDeletionResult(
    int EventsDeleted,
    int ReportsDeleted,
    int JobsDeleted,
    int VoiceCommandsDeleted,
    int MediaFilesDeleted)
{
    public int Total => EventsDeleted + ReportsDeleted + JobsDeleted + VoiceCommandsDeleted + MediaFilesDeleted;

    public static DataDeletionResult Empty { get; } = new(0, 0, 0, 0, 0);
}
