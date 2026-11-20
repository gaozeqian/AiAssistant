using System.Globalization;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.UseCases;

/// <summary>
/// Application use case for querying timeline events, on-demand decrypted media, and deleting user data.
/// Desktop ViewModels depend only on this and sibling use cases—never directly on Storage or Platform.
/// </summary>
public sealed class TimelineUseCase(
    IEventRepository eventRepository,
    IMediaStore mediaStore,
    IClock clock)
{
    public DateOnly GetTodayLocalDate() => clock.Now().LocalDate;

    public async Task<IReadOnlyList<ActivityEvent>> GetEventsForDateAsync(
        DateOnly localDate,
        string? keyword = null,
        CancellationToken ct = default)
    {
        var list = new List<ActivityEvent>();
        var query = new EventQuery(LocalDate: localDate, SearchKeyword: keyword, Limit: 1000);
        await foreach (var item in eventRepository.QueryAsync(query, ct))
        {
            list.Add(item);
        }

        return list;
    }

    public Task<IReadOnlyList<DateOnly>> ListActiveDatesAsync(CancellationToken ct = default) =>
        eventRepository.ListActiveDatesAsync(ct);

    public Task<byte[]?> LoadDecryptedMediaAsync(string contentId, CancellationToken ct = default) =>
        mediaStore.ReadDecryptedAsync(contentId, ct);

    public Task<bool> DeleteEventAsync(string eventId, CancellationToken ct = default) =>
        eventRepository.DeleteEventAsync(eventId, ct);

    public Task<int> PurgeDateAsync(DateOnly localDate, CancellationToken ct = default) =>
        eventRepository.PurgeDateAsync(localDate, ct);
}

public sealed class CaptureControlUseCase(
    ICaptureCoordinator coordinator,
    IAutoStartService autoStartService)
{
    public bool IsPaused => coordinator.IsPaused;
    public IReadOnlyList<string> BlacklistedPatterns => coordinator.BlacklistedPatterns;
    public event EventHandler<CapturePipelineMetrics>? MetricsUpdated
    {
        add => coordinator.MetricsUpdated += value;
        remove => coordinator.MetricsUpdated -= value;
    }

    public CapturePipelineMetrics GetMetrics() => coordinator.GetMetricsSnapshot();

    public Task TogglePauseAsync(CancellationToken ct = default) =>
        coordinator.IsPaused
            ? coordinator.ResumeAsync(ct)
            : coordinator.PauseAsync("User toggled pause", ct);

    public Task PauseAsync(string reason, CancellationToken ct = default) =>
        coordinator.PauseAsync(reason, ct);

    public Task ResumeAsync(CancellationToken ct = default) =>
        coordinator.ResumeAsync(ct);

    public Task UpdateBlacklistAsync(IReadOnlyList<string> patterns, CancellationToken ct = default) =>
        coordinator.SetBlacklistedPatternsAsync(patterns, ct);

    public Task CaptureSnapshotNowAsync(CancellationToken ct = default) =>
        coordinator.CaptureImmediateSnapshotAsync(ct);

    public CapabilityState AutoStartCapability => autoStartService.Capability;
    public Task<bool> IsAutoStartEnabledAsync(CancellationToken ct = default) => autoStartService.IsEnabledAsync(ct);
    public Task SetAutoStartEnabledAsync(bool enabled, CancellationToken ct = default) => autoStartService.SetEnabledAsync(enabled, ct);
}

public sealed record SystemDiagnosticsSnapshot(
    string PlatformName,
    DatabaseHealthReport DatabaseHealth,
    CapturePipelineMetrics PipelineMetrics,
    IReadOnlyList<CapabilityState> Capabilities,
    IReadOnlyList<ReportJob> RecentJobs,
    MediaRetentionMetrics? RetentionMetrics = null,
    IReadOnlyList<DiagnosticLogEntry>? RecentLogs = null,
    ModelCapabilitySnapshot? ModelCapability = null,
    IReadOnlyList<ModelAuditEntry>? ModelAudit = null,
    IReadOnlyList<ModelEndpointCapability>? ModelEndpoints = null);

public sealed class DiagnosticsUseCase(
    ICapabilityProvider capabilityProvider,
    IDatabaseMaintenanceService maintenanceService,
    ICaptureCoordinator coordinator,
    IReportJobQueue jobQueue,
    IRetentionService? retentionService = null,
    IDiagnosticLogReader? logReader = null,
    IModelGateway? modelGateway = null,
    IModelAuditSink? modelAuditSink = null,
    IModelCapabilityProber? modelCapabilityProber = null)
{
    private static readonly JsonSerializerOptions PreviewJsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<SystemDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var dbHealth = await maintenanceService.CheckHealthAsync(ct);
        var jobs = await jobQueue.ListRecentJobsAsync(20, ct);
        var retention = retentionService?.GetMetricsSnapshot();
        var recentLogs = logReader?.GetRecentEntries(30) ?? [];
        ModelCapabilitySnapshot? modelCapability = null;
        if (modelGateway is not null)
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                modelCapability = await modelGateway.ProbeAsync(probeCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                modelCapability = new ModelCapabilitySnapshot(
                    false,
                    false,
                    modelGateway.ChatModelId,
                    modelGateway.EmbeddingModelId,
                    ModelEndpointTier.LocalOffline,
                    "Model probe timed out.");
            }
        }

        IReadOnlyList<ModelEndpointCapability>? modelEndpoints = null;
        if (modelCapabilityProber is not null)
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                modelEndpoints = await modelCapabilityProber.ProbeAllAsync(forceRefresh: false, probeCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                modelEndpoints = null;
            }
        }

        return new SystemDiagnosticsSnapshot(
            capabilityProvider.PlatformName,
            dbHealth,
            coordinator.GetMetricsSnapshot(),
            capabilityProvider.GetCapabilities(),
            jobs,
            retention,
            recentLogs,
            modelCapability,
            modelAuditSink?.GetRecent(20),
            modelEndpoints);
    }

    /// <summary>
    /// Builds a sanitized, metadata-only JSON preview so the user can inspect every field before exporting (IMPLEMENTATION.md §7.3).
    /// </summary>
    public async Task<string> BuildSanitizedExportPreviewAsync(CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct);
        var payload = new
        {
            exportedAtUtc = DateTimeOffset.UtcNow,
            platform = snapshot.PlatformName,
            schemaVersion = snapshot.DatabaseHealth.SchemaVersion.Number,
            database = new
            {
                isHealthy = snapshot.DatabaseHealth.IsHealthy,
                cipher = snapshot.DatabaseHealth.CipherDescription,
                vectorBackend = snapshot.DatabaseHealth.VectorBackend,
                eventCount = snapshot.DatabaseHealth.EventCount,
                encryptedBlobCount = snapshot.DatabaseHealth.EncryptedBlobCount
            },
            capturePipeline = snapshot.PipelineMetrics,
            retention = snapshot.RetentionMetrics,
            capabilities = snapshot.Capabilities.Select(c => new
            {
                kind = c.Kind.ToString(),
                status = c.Status.ToString(),
                summary = c.Summary
            }),
            recentJobs = snapshot.RecentJobs.Select(j => new
            {
                targetDate = j.TargetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                status = j.Status.ToString(),
                attempts = j.Attempts
            }),
            recentSanitizedLogs = snapshot.RecentLogs ?? []
        };

        return JsonSerializer.Serialize(payload, PreviewJsonOptions);
    }

    public async Task ExportConfirmedPreviewAsync(string destinationPath, string confirmedJsonPreview, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmedJsonPreview);

        if (logReader is not null)
        {
            await logReader.ExportConfirmedPreviewAsync(destinationPath, confirmedJsonPreview, ct);
            return;
        }

        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(destinationPath, confirmedJsonPreview, ct);
    }
}
