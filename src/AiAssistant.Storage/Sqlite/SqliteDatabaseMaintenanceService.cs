using System.Globalization;
using System.IO.Compression;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Exceptions;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Storage;
using Microsoft.Data.Sqlite;

namespace AiAssistant.Storage.Sqlite;

/// <summary>
/// Manages database bootstrap migrations, integrity checks, encrypted online backups, and safe restores.
/// </summary>
public sealed class SqliteDatabaseMaintenanceService(EncryptedSqliteConnectionFactory connectionFactory) : IDatabaseMaintenanceService
{
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

    public async Task BackupEncryptedAsync(string destinationPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var parent = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        // Backups are self-contained: encrypted DB + the wrapped master-key envelope + encrypted media,
        // so a restore into another data directory on the same machine can decrypt everything.
        var tempDir = Path.Combine(Path.GetTempPath(), $"aiassistant-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var tempDb = Path.Combine(tempDir, connectionFactory.Options.DatabaseFileName);
            await using (var sourceConn = await connectionFactory.OpenConnectionAsync(ct))
            await using (var destConn = await connectionFactory.OpenConnectionForPathAsync(tempDb, ct))
            {
                sourceConn.BackupDatabase(destConn);
                await destConn.CloseAsync();
            }

            using var zip = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(tempDb, "database/aiassistant.enc.db", CompressionLevel.Optimal);
            AddDirectoryToZip(zip, connectionFactory.Options.KeyStoreDirectory, "keystore");
            AddDirectoryToZip(zip, connectionFactory.Options.MediaDirectory, "media");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    public async Task RestoreEncryptedAsync(string backupSourcePath, CancellationToken ct)
    {
        if (!File.Exists(backupSourcePath))
        {
            throw new FileNotFoundException("Encrypted backup file does not exist.", backupSourcePath);
        }

        if (!IsZipArchive(backupSourcePath))
        {
            await RestoreLegacyDatabaseFileAsync(backupSourcePath, ct);
            return;
        }

        var tempDir = CreateRestoreTempDirectory();
        try
        {
            var extractedDb = await ExtractAndVerifyArchiveAsync(backupSourcePath, tempDir, ct);

            var dbPath = connectionFactory.Options.DatabasePath;
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            DeleteDatabaseSidecars(dbPath);
            File.Copy(extractedDb, dbPath, overwrite: true);

            ReplaceDirectoryContents(Path.Combine(tempDir, "keystore"), connectionFactory.Options.KeyStoreDirectory);
            ReplaceDirectoryContents(Path.Combine(tempDir, "media"), connectionFactory.Options.MediaDirectory);

            // Apply forward migrations if the backup was from an older schema version.
            await InitializeAsync(ct);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    /// <summary>
    /// Validates the archive without touching live data, then leaves it next to the database so the
    /// next start can replace everything while no worker holds the database open.
    /// </summary>
    public async Task StageRestoreAsync(string backupSourcePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupSourcePath);
        var fullPath = Path.GetFullPath(backupSourcePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Encrypted backup file does not exist.", fullPath);
        }

        if (IsZipArchive(fullPath))
        {
            var tempDir = CreateRestoreTempDirectory();
            try
            {
                _ = await ExtractAndVerifyArchiveAsync(fullPath, tempDir, ct);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }
        else
        {
            // A legacy raw database is only readable with the live key envelope.
            await VerifyRestorableAsync(connectionFactory, fullPath, ct);
        }

        var stagedPath = StagedRestorePath;
        Directory.CreateDirectory(connectionFactory.Options.DataDirectory);
        if (!string.Equals(fullPath, stagedPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(fullPath, stagedPath, overwrite: true);
        }
    }

    public async Task<bool> ApplyStagedRestoreAsync(CancellationToken ct)
    {
        var stagedPath = StagedRestorePath;
        if (!File.Exists(stagedPath))
        {
            return false;
        }

        try
        {
            await RestoreEncryptedAsync(stagedPath, ct);
            return true;
        }
        finally
        {
            // Always drop the marker: a failing archive must not block every future start.
            TryDeleteFile(stagedPath);
        }
    }

    public Task<bool> DiscardStagedRestoreAsync(CancellationToken ct)
    {
        var stagedPath = StagedRestorePath;
        if (!File.Exists(stagedPath))
        {
            return Task.FromResult(false);
        }

        TryDeleteFile(stagedPath);
        return Task.FromResult(!File.Exists(stagedPath));
    }

    private string StagedRestorePath =>
        Path.Combine(connectionFactory.Options.DataDirectory, "restore-pending.zip");

    private static string CreateRestoreTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"aiassistant-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private async Task<string> ExtractAndVerifyArchiveAsync(
        string backupPath,
        string tempDir,
        CancellationToken ct)
    {
        ZipFile.ExtractToDirectory(backupPath, tempDir);
        var extractedDb = Path.Combine(tempDir, "database", "aiassistant.enc.db");
        if (!File.Exists(extractedDb))
        {
            throw new StorageIntegrityException("Backup archive does not contain database/aiassistant.enc.db.");
        }

        // Verify with the backup's own keystore BEFORE touching the live data directory. On this machine
        // the OS KEK still unwraps the envelope; a backup from another machine fails here cleanly.
        var tempFactory = new EncryptedSqliteConnectionFactory(
            new StorageOptions { DataDirectory = tempDir },
            connectionFactory.KeyStore.CreateForDirectory(tempDir));
        await VerifyRestorableAsync(tempFactory, extractedDb, ct);
        return extractedDb;
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

    /// <summary>Legacy path: a raw SQLite3MC database file, restorable only with the live key envelope.</summary>
    private async Task RestoreLegacyDatabaseFileAsync(string backupSourcePath, CancellationToken ct)
    {
        await VerifyRestorableAsync(connectionFactory, backupSourcePath, ct);

        var dbPath = connectionFactory.Options.DatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        DeleteDatabaseSidecars(dbPath);
        File.Copy(backupSourcePath, dbPath, overwrite: true);
        await InitializeAsync(ct);
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

    private static bool IsZipArchive(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            if (file.Read(magic) < 4)
            {
                return false;
            }

            return magic[0] == 0x50 && magic[1] == 0x4B && (magic[2] == 0x03 || magic[2] == 0x05 || magic[2] == 0x07);
        }
        catch (IOException)
        {
            return false;
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
