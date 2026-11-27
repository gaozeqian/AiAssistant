namespace AiAssistant.Storage;

public sealed class StorageOptions
{
    public required string DataDirectory { get; init; }
    public string DatabaseFileName { get; init; } = "aiassistant.enc.db";
    public string MasterKeyId { get; init; } = "primary-storage-master-key";

    /// <summary>Default expiry for newly written media; updated live from the retention settings.</summary>
    public TimeSpan DefaultMediaRetention { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan RetentionSweepInterval { get; init; } = TimeSpan.FromHours(1);
    public long MaxDiagnosticLogBytes { get; init; } = 512 * 1024;

    /// <summary>Hard cap for the whole data directory; null means unlimited.</summary>
    public long? MaxStorageQuotaBytes { get; set; }
    public Action? FaultBeforeEventTransactionCommit { get; set; }

    public string DatabasePath => Path.Combine(DataDirectory, DatabaseFileName);
    public string MediaDirectory => Path.Combine(DataDirectory, "media");
    public string KeyStoreDirectory => Path.Combine(DataDirectory, "keystore");
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
}
