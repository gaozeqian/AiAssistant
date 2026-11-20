using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record EventQuery(
    DateOnly? LocalDate = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    SourceKind? Source = null,
    string? SearchKeyword = null,
    int Limit = 500,
    DateOnly? FromLocalDate = null,
    DateOnly? ToLocalDate = null);

public sealed record EventWithMediaWrite(
    ActivityEvent Event,
    byte[]? RawMediaBytes = null,
    ContentKind? MediaKind = null,
    TimeSpan? MediaRetention = null);

public interface IEventRepository
{
    Task AppendBatchAsync(IReadOnlyList<ActivityEvent> events, CancellationToken ct);

    /// <summary>
    /// Atomically persists events and their encrypted media blobs so crash/interruption never leaves dangling references.
    /// </summary>
    Task AppendWithMediaBatchAsync(IReadOnlyList<EventWithMediaWrite> items, CancellationToken ct);

    IAsyncEnumerable<ActivityEvent> QueryAsync(EventQuery query, CancellationToken ct);

    Task<ActivityEvent?> GetByIdAsync(string eventId, CancellationToken ct);

    Task<IReadOnlyList<DateOnly>> ListActiveDatesAsync(CancellationToken ct);

    Task<bool> DeleteEventAsync(string eventId, CancellationToken ct);

    Task<int> PurgeDateAsync(DateOnly localDate, CancellationToken ct);
}

public sealed record ContentWriteRequest(
    string ContentId,
    ContentKind Kind,
    ReadOnlyMemory<byte> Plaintext,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc = null,
    ulong? PerceptualHash = null);

public interface IMediaStore
{
    Task<ContentRef> PutAsync(ContentWriteRequest request, CancellationToken ct);
    Task<byte[]?> ReadDecryptedAsync(string contentId, CancellationToken ct);
    Task<int> IncrementRefAsync(string contentId, CancellationToken ct);
    Task<int> DecrementRefAsync(string contentId, CancellationToken ct);
    Task<int> PurgeExpiredAsync(DateTimeOffset nowUtc, CancellationToken ct);
}

public interface IKeyStore
{
    CapabilityState Capability { get; }
    Task<byte[]> GetOrCreateMasterKeyAsync(string keyId, CancellationToken ct);
    Task RotateWrapperKeyAsync(string keyId, CancellationToken ct);
}

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

/// <summary>User-initiated local data deletion. Never touches the OS key store or model configuration.</summary>
public interface IDataDeletionService
{
    /// <summary>Deletes events, their encrypted media, reports and queued jobs for one local date.</summary>
    Task<DataDeletionResult> PurgeDateAsync(DateOnly localDate, CancellationToken ct);

    /// <summary>Deletes all captured events, media, reports, jobs, voice logs and vector rows.</summary>
    Task<DataDeletionResult> PurgeAllAsync(CancellationToken ct);
}

public sealed record VectorModelDescriptor(string ModelId, string ModelVersion, int Dimensions);

public sealed record VectorIndexMetadata(
    string BackendName,
    bool IsNativeSqliteVecLoaded,
    VectorModelDescriptor? ActiveModel,
    long IndexedCount);

public sealed record VectorSearchHit(string EventId, float Score);

public interface IVectorIndex
{
    Task<VectorIndexMetadata> GetMetadataAsync(CancellationToken ct);
    Task UpsertAsync(string eventId, ReadOnlyMemory<float> embedding, VectorModelDescriptor model, CancellationToken ct);
    Task<IReadOnlyList<VectorSearchHit>> SearchAsync(ReadOnlyMemory<float> queryEmbedding, int topK, CancellationToken ct);
    Task RebuildAsync(
        VectorModelDescriptor targetModel,
        Func<CancellationToken, IAsyncEnumerable<(string EventId, ReadOnlyMemory<float> Embedding)>> generator,
        CancellationToken ct);
}

public sealed record ReportJobRequest(
    DateOnly TargetDate,
    ReportTriggerSource TriggerSource,
    bool ForceOverwrite = false);

public sealed record EnqueueResult(string JobId, bool DeduplicatedExistingPendingJob);

public interface IReportJobQueue
{
    Task<EnqueueResult> EnqueueAsync(ReportJobRequest request, CancellationToken ct);
    Task<int> RecoverInterruptedJobsAsync(CancellationToken ct);
    Task<ReportJob?> ClaimNextPendingAsync(CancellationToken ct);
    Task UpdateAsync(ReportJob job, CancellationToken ct);
    Task<IReadOnlyList<ReportJob>> ListRecentJobsAsync(int limit, CancellationToken ct);
}

public sealed record DatabaseHealthReport(
    bool IsHealthy,
    SchemaVersion SchemaVersion,
    string CipherDescription,
    string VectorBackend,
    long EventCount,
    long EncryptedBlobCount,
    string IntegrityMessage);

public interface IDatabaseMaintenanceService
{
    Task InitializeAsync(CancellationToken ct);
    Task<DatabaseHealthReport> CheckHealthAsync(CancellationToken ct);
    Task BackupEncryptedAsync(string destinationPath, CancellationToken ct);
    Task RestoreEncryptedAsync(string backupSourcePath, CancellationToken ct);
}
