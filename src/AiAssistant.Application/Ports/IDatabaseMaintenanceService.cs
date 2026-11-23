using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IDatabaseMaintenanceService
{
    Task InitializeAsync(CancellationToken ct);
    Task<DatabaseHealthReport> CheckHealthAsync(CancellationToken ct);
    Task BackupEncryptedAsync(string destinationPath, CancellationToken ct);
    Task RestoreEncryptedAsync(string backupSourcePath, CancellationToken ct);
}
