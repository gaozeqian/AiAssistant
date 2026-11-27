using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Exceptions;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Storage;
using AiAssistant.Storage.Backup;
using AiAssistant.Storage.KeyStores;
using Microsoft.Data.Sqlite;

namespace AiAssistant.Storage.Sqlite;

/// <summary>
/// Manages database bootstrap migrations, integrity checks, portable encrypted backups and safe restores.
/// </summary>
public sealed class SqliteDatabaseMaintenanceService(EncryptedSqliteConnectionFactory connectionFactory) : IDatabaseMaintenanceService
{
    private const string StagingFolderName = "restore-pending";
    private const string ReadyMarkerName = "READY";
    private const string KeystoreReadyMarkerName = "keystore.ready";
    private const string LegacyStagedRestoreFileName = "restore-pending.zip";
    private const string PayloadDatabaseEntry = "database/aiassistant.enc.db";

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        _ = await SchemaMigrator.MigrateAsync(conn, ct);
    }

    public async Task<DatabaseHealthReport> CheckHealthAsync(CancellationToken ct)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        var version = await SchemaMigrator.GetCurrentVersionAsync(conn, ct);

        string integrity;
        await using (var integrityCmd = conn.CreateCommand())
        {
            integrityCmd.CommandText = "PRAGMA integrity_check;";
            integrity = (await integrityCmd.ExecuteScalarAsync(ct))?.ToString() ?? "unknown";
        }

        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new StorageIntegrityException($"SQLite integrity_check failed: {integrity}");
        }

        long eventCount;
        await using (var evCmd = conn.CreateCommand())
        {
            evCmd.CommandText = "SELECT count(*) FROM events;";
            eventCount = Convert.ToInt64(await evCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        long blobCount;
        await using (var blobCmd = conn.CreateCommand())
        {
            blobCmd.CommandText = "SELECT count(*) FROM content_refs;";
            blobCount = Convert.ToInt64(await blobCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        return new DatabaseHealthReport(
            IsHealthy: true,
            SchemaVersion: version,
            CipherDescription: "SQLite3MC (AES-256 / ChaCha20-Poly1305)",
            VectorBackend: connectionFactory.VectorBackendDiagnostic,
            EventCount: eventCount,
            EncryptedBlobCount: blobCount,
            IntegrityMessage: integrity);
    }

    public string CreateBackupKey() => BackupKeyCodec.CreateKey();

    /// <summary>
    /// The online backup and ZIP compression are synchronous and can take seconds on a large data
    /// directory; running them on the caller's thread would freeze the desktop UI. The whole core is
    /// therefore dispatched to the thread pool.
    /// </summary>
    public Task BackupEncryptedAsync(string destinationPath, string backupKey, CancellationToken ct) =>
        Task.Run(() => BackupEncryptedCoreAsync(destinationPath, backupKey, ct), ct);

    private async Task BackupEncryptedCoreAsync(string destinationPath, string backupKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupKey);
        var parent = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        var tempDir = CreateTempDirectory("aiassistant-backup");
        try
        {
            var tempDb = Path.Combine(tempDir, "aiassistant.enc.db");
            await using (var sourceConn = await connectionFactory.OpenConnectionAsync(ct))
            await using (var destConn = await connectionFactory.OpenConnectionForPathAsync(tempDb, ct))
            {
                sourceConn.BackupDatabase(destConn);
                await destConn.CloseAsync();
            }

            RestrictToCurrentUser(tempDb, isDirectory: false);
            var masterKey = await connectionFactory.KeyStore.GetOrCreateMasterKeyAsync(
                connectionFactory.Options.MasterKeyId,
                ct);
            try
            {
                var payloadZip = Path.Combine(tempDir, "payload.zip");
                await using (var payloadStream = new FileStream(
                                 payloadZip,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None))
                using (var zip = new ZipArchive(payloadStream, ZipArchiveMode.Create, leaveOpen: false))
                {
                    zip.CreateEntryFromFile(tempDb, PayloadDatabaseEntry, CompressionLevel.Optimal);
                    AddDirectoryToZip(zip, connectionFactory.Options.MediaDirectory, "media");
                    var keyEntry = zip.CreateEntry("master.key", CompressionLevel.NoCompression);
                    await using var keyStream = keyEntry.Open();
                    await keyStream.WriteAsync(masterKey, ct);
                }

                RestrictToCurrentUser(payloadZip, isDirectory: false);
                try
                {
                    await PortableBackupArchive.WriteAsync(destinationPath, backupKey, payloadZip, ct);
                }
                catch
                {
                    TryDeleteFile(destinationPath);
                    throw;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    /// <summary>Immediate restore used by the CLI; the desktop uses stage/commit/apply instead.</summary>
    public Task RestoreEncryptedAsync(string backupSourcePath, string backupKey, CancellationToken ct) =>
        Task.Run(
            async () =>
            {
                await StageRestoreCoreAsync(backupSourcePath, backupKey, ct);
                if (!CommitStagedRestoreCore())
                {
                    throw new StorageIntegrityException("Failed to commit the staged restore.");
                }

                if (!ApplyStagedRestoreCore(ct))
                {
                    throw new StorageIntegrityException("Failed to apply the staged restore.");
                }
            },
            ct);

    /// <summary>
    /// Validates the archive and prepares the staged database, media and a keystore re-wrapped for
    /// this machine. Nothing is applied until the caller commits the staging.
    /// </summary>
    public Task StageRestoreAsync(string backupSourcePath, string backupKey, CancellationToken ct) =>
        Task.Run(() => StageRestoreCoreAsync(backupSourcePath, backupKey, ct), ct);

    private async Task StageRestoreCoreAsync(string backupSourcePath, string backupKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupSourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupKey);
        var fullPath = Path.GetFullPath(backupSourcePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Encrypted backup file does not exist.", fullPath);
        }

        var tempDir = CreateTempDirectory("aiassistant-restore");
        try
        {
            var masterKey = await PortableBackupArchive.ExtractAsync(fullPath, backupKey, tempDir, ct);
            try
            {
                var extractedDb = Path.Combine(tempDir, PayloadDatabaseEntry);
                if (!File.Exists(extractedDb))
                {
                    throw new StorageIntegrityException("Backup payload does not contain the database.");
                }

                var tempFactory = new EncryptedSqliteConnectionFactory(
                    new StorageOptions { DataDirectory = tempDir },
                    new StaticKeyStore(masterKey));
                await VerifyRestorableAsync(tempFactory, extractedDb, ct);

                var stagingDir = StagingDirectory;
                if (Directory.Exists(stagingDir))
                {
                    Directory.Delete(stagingDir, recursive: true);
                }

                Directory.CreateDirectory(stagingDir);
                var stagedDb = Path.Combine(stagingDir, PayloadDatabaseEntry);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedDb)!);
                File.Copy(extractedDb, stagedDb, overwrite: true);
                ReplaceDirectoryContents(Path.Combine(tempDir, "media"), Path.Combine(stagingDir, "media"));

                // Bind the backup's master key to this machine's OS keystore. The staged keystore is
                // swapped in together with the database, so the next start needs no backup key.
                var stagingKeyStore = connectionFactory.KeyStore.CreateForDirectory(stagingDir);
                await stagingKeyStore.ImportMasterKeyAsync(
                    connectionFactory.Options.MasterKeyId,
                    masterKey,
                    ct);

                // A key store may not use a directory at all (the in-memory test store does not), so
                // record that the key was re-wrapped instead of inferring it from the directory.
                File.WriteAllText(Path.Combine(stagingDir, KeystoreReadyMarkerName), "1");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    public Task<bool> CommitStagedRestoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(CommitStagedRestoreCore());
    }

    private bool CommitStagedRestoreCore()
    {
        var stagingDir = StagingDirectory;
        if (!File.Exists(Path.Combine(stagingDir, PayloadDatabaseEntry)) ||
            !File.Exists(Path.Combine(stagingDir, KeystoreReadyMarkerName)))
        {
            return false;
        }

        File.WriteAllText(Path.Combine(stagingDir, ReadyMarkerName), "ready");
        return true;
    }

    public Task<bool> ApplyStagedRestoreAsync(CancellationToken ct) =>
        Task.Run(() => ApplyStagedRestoreCore(ct), ct);

    private bool ApplyStagedRestoreCore(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Same-machine archives from 0.1.6 and earlier are no longer supported; drop a stale marker
        // so it cannot be mistaken for a portable restore.
        TryDeleteFile(LegacyStagedRestorePath);

        var stagingDir = StagingDirectory;
        if (!File.Exists(Path.Combine(stagingDir, ReadyMarkerName)))
        {
            TryDeleteDirectory(stagingDir);
            return false;
        }

        var stagedDb = Path.Combine(stagingDir, PayloadDatabaseEntry);
        var dbPath = connectionFactory.Options.DatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        DeleteDatabaseSidecars(dbPath);
        File.Copy(stagedDb, dbPath, overwrite: true);
        ReplaceDirectoryContents(Path.Combine(stagingDir, "media"), connectionFactory.Options.MediaDirectory);
        var stagedKeystore = Path.Combine(stagingDir, "keystore");
        if (Directory.Exists(stagedKeystore))
        {
            ReplaceDirectoryContents(stagedKeystore, connectionFactory.Options.KeyStoreDirectory);
        }

        TryDeleteDirectory(stagingDir);
        return true;
    }

    public Task<bool> DiscardStagedRestoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var existed = Directory.Exists(StagingDirectory) || File.Exists(LegacyStagedRestorePath);
        TryDeleteDirectory(StagingDirectory);
        TryDeleteFile(LegacyStagedRestorePath);
        return Task.FromResult(existed);
    }

    private string StagingDirectory =>
        Path.Combine(connectionFactory.Options.DataDirectory, StagingFolderName);

    private string LegacyStagedRestorePath =>
        Path.Combine(connectionFactory.Options.DataDirectory, LegacyStagedRestoreFileName);

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        RestrictToCurrentUser(path, isDirectory: true);
        return path;
    }

    private static void RestrictToCurrentUser(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                isDirectory
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
            // Best effort: the temporary directory is deleted as soon as the operation finishes.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException ex)
        {
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace(
                "Storage.Sqlite.SqliteDatabaseMaintenanceService.TryDeleteDirectory",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace(
                "Storage.Sqlite.SqliteDatabaseMaintenanceService.TryDeleteDirectory",
                ex);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace(
                "Storage.Sqlite.SqliteDatabaseMaintenanceService.TryDeleteFile",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace(
                "Storage.Sqlite.SqliteDatabaseMaintenanceService.TryDeleteFile",
                ex);
        }
    }

    private static async Task VerifyRestorableAsync(
        EncryptedSqliteConnectionFactory factory,
        string databasePath,
        CancellationToken ct)
    {
        await using var verifyConn = await factory.OpenConnectionForPathAsync(databasePath, ct);
        await using (var cmd = verifyConn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA integrity_check;";
            var status = (await cmd.ExecuteScalarAsync(ct))?.ToString();
            if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new StorageIntegrityException($"Refusing to restore corrupted backup '{databasePath}': {status}");
            }
        }

        var backupVersion = await SchemaMigrator.GetCurrentVersionAsync(verifyConn, ct);
        if (backupVersion > SchemaVersion.Current)
        {
            throw new SchemaMigrationException(
                $"Backup schema version ({backupVersion.Number}) is newer than current application ({SchemaVersion.Current.Number}).");
        }
    }

    private static void DeleteDatabaseSidecars(string dbPath)
    {
        foreach (var sidecar in new[] { $"{dbPath}-wal", $"{dbPath}-shm" })
        {
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
        }
    }

    private static void AddDirectoryToZip(ZipArchive zip, string directory, string prefix)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
            zip.CreateEntryFromFile(file, $"{prefix}/{relative}", CompressionLevel.Optimal);
        }
    }

    private static void ReplaceDirectoryContents(string sourceDir, string destinationDir)
    {
        if (Directory.Exists(destinationDir))
        {
            Directory.Delete(destinationDir, recursive: true);
        }

        if (!Directory.Exists(sourceDir))
        {
            Directory.CreateDirectory(destinationDir);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationDir)!);
        CopyDirectory(sourceDir, destinationDir);
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectory(directory, Path.Combine(destinationDir, Path.GetFileName(directory)));
        }
    }
}
