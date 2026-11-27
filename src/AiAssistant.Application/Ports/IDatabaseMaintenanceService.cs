using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IDatabaseMaintenanceService
{
    Task InitializeAsync(CancellationToken ct);
    Task<DatabaseHealthReport> CheckHealthAsync(CancellationToken ct);

    /// <summary>
    /// Creates a new 256-bit portable-backup key. The key is displayed to the user exactly once and
    /// is required to restore the archive on another machine.
    /// </summary>
    string CreateBackupKey();

    /// <summary>
    /// Writes a portable encrypted backup. The archive carries the database, media and the master
    /// key, all encrypted under <paramref name="backupKey"/>, so it can be restored on another
    /// machine without this machine's OS keystore.
    /// </summary>
    Task BackupEncryptedAsync(string destinationPath, string backupKey, CancellationToken ct);

    /// <summary>Decrypts and restores a portable backup immediately (used by the CLI).</summary>
    Task RestoreEncryptedAsync(string backupSourcePath, string backupKey, CancellationToken ct);

    /// <summary>
    /// Decrypts, validates and prepares a portable backup for the next start. The staged data is not
    /// applied until <see cref="CommitStagedRestoreAsync"/> confirms it, so a cancelled confirmation
    /// leaves the current data untouched.
    /// </summary>
    Task StageRestoreAsync(string backupSourcePath, string backupKey, CancellationToken ct);

    /// <summary>Marks a validated staged restore as ready to apply on the next start.</summary>
    Task<bool> CommitStagedRestoreAsync(CancellationToken ct);

    /// <summary>
    /// Applies a committed staged restore and removes the staging directory afterwards.
    /// Returns <c>true</c> when local data was replaced.
    /// </summary>
    Task<bool> ApplyStagedRestoreAsync(CancellationToken ct);

    /// <summary>
    /// Drops a staged restore so the next start keeps the current data. Returns <c>true</c> when
    /// staged data was actually removed.
    /// </summary>
    Task<bool> DiscardStagedRestoreAsync(CancellationToken ct);
}
