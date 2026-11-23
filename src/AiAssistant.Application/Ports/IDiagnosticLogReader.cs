using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IDiagnosticLogReader
{
    IReadOnlyList<DiagnosticLogEntry> GetRecentEntries(int maxCount = 50);
    Task ExportConfirmedPreviewAsync(string destinationPath, string confirmedJsonPreview, CancellationToken ct);
}
