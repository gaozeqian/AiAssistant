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
    IReadOnlyList<ReportJob> RecentJobs);

public sealed class DiagnosticsUseCase(
    ICapabilityProvider capabilityProvider,
    IDatabaseMaintenanceService maintenanceService,
    ICaptureCoordinator coordinator,
    IReportJobQueue jobQueue)
{
    public async Task<SystemDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var dbHealth = await maintenanceService.CheckHealthAsync(ct);
        var jobs = await jobQueue.ListRecentJobsAsync(20, ct);
        return new SystemDiagnosticsSnapshot(
            capabilityProvider.PlatformName,
            dbHealth,
            coordinator.GetMetricsSnapshot(),
            capabilityProvider.GetCapabilities(),
            jobs);
    }
}
