using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IDatabaseMaintenanceService
{
    Task InitializeAsync(CancellationToken ct);
    Task<DatabaseHealthReport> CheckHealthAsync(CancellationToken ct);
    Task BackupEncryptedAsync(string destinationPath, CancellationToken ct);
    Task RestoreEncryptedAsync(string backupSourcePath, CancellationToken ct);

    /// <summary>
    /// Validates a backup archive and stages it to be applied on the next application start.
    /// Restoring replaces the live database, key envelope and media, so it must not run while
    /// capture or report workers still hold the database open.
    /// </summary>
    Task StageRestoreAsync(string backupSourcePath, CancellationToken ct);

    /// <summary>
    /// Applies a staged restore, if one exists, and removes the staged archive afterwards.
    /// Returns <c>true</c> when local data was replaced.
    /// </summary>
    Task<bool> ApplyStagedRestoreAsync(CancellationToken ct);

    /// <summary>
    /// Drops a staged restore so the next start keeps the current data. Returns <c>true</c> when
    /// a staged archive was actually removed.
    /// </summary>
    Task<bool> DiscardStagedRestoreAsync(CancellationToken ct);
}
