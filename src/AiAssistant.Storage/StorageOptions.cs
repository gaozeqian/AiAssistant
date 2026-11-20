namespace AiAssistant.Storage;

public sealed class StorageOptions
{
    public required string DataDirectory { get; init; }
    public string DatabaseFileName { get; init; } = "aiassistant.enc.db";
    public string MasterKeyId { get; init; } = "primary-storage-master-key";
    public TimeSpan DefaultMediaRetention { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan RetentionSweepInterval { get; init; } = TimeSpan.FromHours(1);
    public long MaxDiagnosticLogBytes { get; init; } = 512 * 1024;
    public long? MaxStorageQuotaBytes { get; set; }
    public Action? FaultBeforeEventTransactionCommit { get; set; }

    public string DatabasePath => Path.Combine(DataDirectory, DatabaseFileName);
    public string MediaDirectory => Path.Combine(DataDirectory, "media");
    public string KeyStoreDirectory => Path.Combine(DataDirectory, "keystore");
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
}
