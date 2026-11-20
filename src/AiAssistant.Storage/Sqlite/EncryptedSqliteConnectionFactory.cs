using System.Runtime.InteropServices;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Exceptions;
using Microsoft.Data.Sqlite;

namespace AiAssistant.Storage.Sqlite;

/// <summary>
/// Initializes SQLite3 Multiple Ciphers (SQLite3MC) and opens encrypted connections with WAL, busy_timeout,
/// key verification, and optional sqlite-vec extension probing (ADR-0003, ADR-0004).
/// </summary>
public sealed class EncryptedSqliteConnectionFactory
{
    private static readonly Lazy<bool> ProviderInitialized = new(() =>
    {
        SQLitePCL.Batteries_V2.Init();
        return true;
    });

    private readonly StorageOptions _options;
    private readonly IKeyStore _keyStore;
    private bool _vecProbeCompleted;
    private bool _isSqliteVecAvailable;
    private string _vecBackendDiagnostic = "NotProbed";

    public EncryptedSqliteConnectionFactory(StorageOptions options, IKeyStore keyStore)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keyStore);
        _ = ProviderInitialized.Value;
        _options = options;
        _keyStore = keyStore;
    }

    public StorageOptions Options => _options;
    public bool IsNativeSqliteVecLoaded => _isSqliteVecAvailable;
    public string VectorBackendDiagnostic => _vecBackendDiagnostic;

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct = default) =>
        await OpenConnectionForPathAsync(_options.DatabasePath, ct);

    public async Task<SqliteConnection> OpenConnectionForPathAsync(string databasePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var parentDir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        // Detect corrupted non-SQLite header vs unencrypted SQLite file before opening
        var masterKey = await _keyStore.GetOrCreateMasterKeyAsync(_options.MasterKeyId, ct);
        var passphrase = Convert.ToHexStringLower(masterKey);
        CryptographicOperationsZero(masterKey);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        };

        var connection = new SqliteConnection(builder.ToString());
        try
        {
            await connection.OpenAsync(ct);

            // Configure SQLite3MC cipher & apply key before any schema access
            await ExecutePragmaAsync(connection, $"PRAGMA key = '{passphrase}';", ct);
            await ExecutePragmaAsync(connection, "PRAGMA busy_timeout = 5000;", ct);
            await ExecutePragmaAsync(connection, "PRAGMA foreign_keys = ON;", ct);

            // Force immediate page 1 decryption & verification; fails with SQLITE_NOTADB (26) if wrong key or corrupt header
            await using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.CommandText = "SELECT count(*) FROM sqlite_schema;";
                _ = await checkCmd.ExecuteScalarAsync(ct);
            }

            await ExecutePragmaAsync(connection, "PRAGMA journal_mode = WAL;", ct);
            TryLoadSqliteVecExtension(connection);

            return connection;
        }
        catch (Exception ex)
        {
            await connection.DisposeAsync();
            throw TranslateSqliteException(ex, databasePath);
        }
    }

    public void EnsureQuotaAllowsWrite(long additionalBytes)
    {
        if (_options.MaxStorageQuotaBytes is not { } maxBytes)
        {
            return;
        }

        var usedBytes = CalculateDirectorySize(_options.DataDirectory);
        if (usedBytes + additionalBytes > maxBytes)
        {
            throw new StorageCapacityException(
                $"Storage capacity limit exceeded ({usedBytes + additionalBytes} bytes > quota {maxBytes} bytes).");
        }
    }

    public static Exception TranslateSqliteException(Exception ex, string path)
    {
        if (ex is AiAssistantDomainException)
        {
            return ex;
        }

        if (ex is SqliteException sqliteEx)
        {
            return sqliteEx.SqliteErrorCode switch
            {
                26 => new StorageAuthenticationException(
                    $"Cannot decrypt or authenticate SQLite3MC database '{Path.GetFileName(path)}' (SQLITE_NOTADB). Verify encryption key or check for header corruption.",
                    sqliteEx),
                11 => new StorageIntegrityException(
                    $"Encrypted SQLite database '{Path.GetFileName(path)}' failed integrity check (SQLITE_CORRUPT).",
                    sqliteEx),
                13 => new StorageCapacityException(
                    $"Disk or database is full while writing '{Path.GetFileName(path)}' (SQLITE_FULL).",
                    sqliteEx),
                8 or 14 => new PlatformPermissionException(
                    $"Insufficient file permissions to access '{Path.GetFileName(path)}' (SQLite code {sqliteEx.SqliteErrorCode}).",
                    sqliteEx),
                _ => sqliteEx
            };
        }

        if (ex is UnauthorizedAccessException uae)
        {
            return new PlatformPermissionException($"OS denied file access for '{Path.GetFileName(path)}'.", uae);
        }

        if (ex is IOException ioEx && IsDiskFullIoException(ioEx))
        {
            return new StorageCapacityException($"Disk full while writing '{Path.GetFileName(path)}'.", ioEx);
        }

        return ex;
    }

    private void TryLoadSqliteVecExtension(SqliteConnection connection)
    {
        try
        {
            var candidatePath = ResolveVec0ExtensionPath();
            if (candidatePath is null)
            {
                if (!_vecProbeCompleted)
                {
                    _vecProbeCompleted = true;
                    _isSqliteVecAvailable = false;
                    _vecBackendDiagnostic = "EncryptedSqliteSimdFallback (vec0 native binary not found in probing paths)";
                }

                return;
            }

            connection.EnableExtensions(true);
            connection.LoadExtension(candidatePath, "sqlite3_vec_init");

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT vec_version();";
            var version = cmd.ExecuteScalar()?.ToString() ?? "unknown";

            _vecProbeCompleted = true;
            _isSqliteVecAvailable = true;
            _vecBackendDiagnostic = $"sqlite-vec ({version}) + EncryptedSqliteMirror";
        }
        catch (Exception ex)
        {
            if (!_vecProbeCompleted)
            {
                _vecProbeCompleted = true;
                _isSqliteVecAvailable = false;
                _vecBackendDiagnostic = $"EncryptedSqliteSimdFallback ({ex.GetType().Name}: {ex.Message})";
            }
        }
    }

    private static string? ResolveVec0ExtensionPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var fileName = OperatingSystem.IsWindows()
            ? "vec0.dll"
            : OperatingSystem.IsMacOS()
                ? "vec0.dylib"
                : "vec0.so";

        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 when OperatingSystem.IsMacOS() => "osx-arm64",
            Architecture.Arm64 when OperatingSystem.IsWindows() => "win-arm64",
            Architecture.Arm64 when OperatingSystem.IsLinux() => "linux-arm64",
            Architecture.X64 when OperatingSystem.IsWindows() => "win-x64",
            Architecture.X64 when OperatingSystem.IsMacOS() => "osx-x64",
            _ => "linux-x64"
        };

        string[] candidates =
        [
            Path.Combine(baseDir, fileName),
            Path.Combine(baseDir, "runtimes", rid, "native", fileName)
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task ExecutePragmaAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static long CalculateDirectorySize(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return 0L;
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch
            {
                // Ignore transient temp files being deleted concurrently
            }
        }

        return total;
    }

    private static bool IsDiskFullIoException(IOException ex)
    {
        const int HrErrorHandleDiskFull = unchecked((int)0x80070027);
        const int HrErrorDiskFull = unchecked((int)0x80070070);
        return ex.HResult is HrErrorHandleDiskFull or HrErrorDiskFull;
    }

    private static void CryptographicOperationsZero(byte[] bytes) =>
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
}
