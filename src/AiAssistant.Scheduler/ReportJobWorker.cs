using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Scheduler;

public sealed class ReportScheduleOptions
{
    public bool EnableStartupCatchUp { get; init; } = true;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
}

public sealed record ReportProgress(
    string JobId,
    DateOnly TargetDate,
    ReportJobStatus Status,
    string Message);

/// <summary>
/// Single-consumer persistent report worker. Manual, startup and scheduled triggers all enter the same
/// idempotent report_jobs queue (ADR-0010). Automatic checks always target the previous local date:
/// a report is created only when that date has activity, and is regenerated as a new version when
/// activity arrived after the latest report was generated.
/// </summary>
public sealed class ReportJobWorker : IAsyncDisposable
{
    private readonly IReportJobQueue _queue;
    private readonly IReportGenerationRunner _runner;
    private readonly IReportRepository _reports;
    private readonly IEventRepository _events;
    private readonly IClock _clock;
    private readonly IReportScheduleSettingsStore _scheduleSettings;
    private readonly ReportScheduleOptions _options;
    private readonly ILogger<ReportJobWorker> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _workerTask;
    private DateOnly? _lastScheduledDate;
    private bool _started;

    public ReportJobWorker(
        IReportJobQueue queue,
        IReportGenerationRunner runner,
        IReportRepository reports,
        IEventRepository events,
        IClock clock,
        IReportScheduleSettingsStore scheduleSettings,
        ReportScheduleOptions options,
        ILogger<ReportJobWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(scheduleSettings);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _queue = queue;
        _runner = runner;
        _reports = reports;
        _events = events;
        _clock = clock;
        _scheduleSettings = scheduleSettings;
        _options = options;
        _logger = logger;
    }

    public event EventHandler<ReportProgress>? ProgressChanged;

    public async Task StartAsync(CancellationToken ct)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        if (_options.EnableStartupCatchUp)
        {
            try
            {
                await EvaluatePreviousDayAsync(ReportTriggerSource.StartupCatchUp, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    "Startup daily-report check failed with {ExceptionType}; the scheduled check will retry.",
                    ex.GetType().Name);
            }
        }

        _workerTask = Task.Run(() => RunAsync(_shutdown.Token), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        if (_workerTask is not null)
        {
            try
            {
                await _workerTask;
            }
            catch (OperationCanceledException ex)
            {
                // Expected shutdown
                AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Scheduler.ReportJobWorker.DisposeAsync", ex);
            }
        }

        _shutdown.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EvaluateScheduledIfDueAsync(ct);
                var job = await _queue.ClaimNextPendingAsync(ct);
                if (job is null)
                {
                    await Task.Delay(_options.PollInterval, ct);
                    continue;
                }

                Raise(job, "正在生成日报");
                var outcome = await _runner.GenerateAsync(
                    new ReportJobRequest(job.TargetDate, job.TriggerSource, job.ForceOverwrite),
                    ct);

                if (outcome.FailureReason is not null)
                {
                    job.RecordFailure(outcome.FailureReason, _clock.UtcNow);
                    await _queue.UpdateAsync(job, ct);
                    Raise(job, "日报生成失败");
                    continue;
                }

                if (outcome.SkippedExisting)
                {
                    job.MarkSkippedAlreadyExists(_clock.UtcNow);
                }
                else
                {
                    job.MarkCompleted(_clock.UtcNow);
                }

                await _queue.UpdateAsync(job, ct);
                Raise(job, outcome.SkippedExisting ? "已存在日报，跳过重复生成" : "日报已生成");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Report worker iteration failed with {ExceptionType}.", ex.GetType().Name);
                await Task.Delay(_options.PollInterval, ct);
            }
        }
    }

    private async Task EvaluatePreviousDayAsync(ReportTriggerSource triggerSource, CancellationToken ct)
    {
        var settings = _scheduleSettings.Load();
        if (!settings.Enabled)
        {
            return;
        }

        var now = _clock.Now();
        await EvaluateDateAsync(now.LocalDate.AddDays(-1), triggerSource, ct);
    }

    private async Task EvaluateScheduledIfDueAsync(CancellationToken ct)
    {
        var settings = _scheduleSettings.Load();
        if (!settings.Enabled)
        {
            return;
        }

        var now = _clock.Now();
        var localTime = TimeOnly.FromDateTime(now.ToCapturedLocalTime().DateTime);
        if (localTime < settings.DailyLocalTime || _lastScheduledDate == now.LocalDate)
        {
            return;
        }

        await EvaluateDateAsync(now.LocalDate.AddDays(-1), ReportTriggerSource.ScheduledDaily, ct);
        // Mark only after a successful check: a transient storage failure must retry on the next tick.
        _lastScheduledDate = now.LocalDate;
    }

    private async Task EvaluateDateAsync(
        DateOnly targetDate,
        ReportTriggerSource triggerSource,
        CancellationToken ct)
    {
        var latest = await _reports.GetLatestAsync(targetDate, ct);
        var forceOverwrite = false;
        if (latest is null)
        {
            if (!await HasActivityAsync(targetDate, fromUtc: null, ct))
            {
                _logger.LogInformation(
                    "Skipping {TriggerSource} daily report for {Date}: no activity records.",
                    triggerSource,
                    targetDate);
                return;
            }
        }
        else
        {
            // The latest version was persisted at GeneratedAtUtc; activity captured afterwards
            // is not in it and requires an appended version.
            var boundary = latest.GeneratedAtUtc.AddTicks(1);
            if (!await HasActivityAsync(targetDate, boundary, ct))
            {
                return;
            }

            forceOverwrite = true;
        }

        if (await HasActiveJobAsync(targetDate, forceOverwrite, ct))
        {
            return;
        }

        var enqueued = await _queue.EnqueueAsync(
            new ReportJobRequest(targetDate, triggerSource, forceOverwrite),
            ct);
        _logger.LogInformation(
            "Queued {TriggerSource} daily report for {Date} (job={JobId}, force={ForceOverwrite}, deduplicated={Deduplicated}).",
            triggerSource,
            targetDate,
            enqueued.JobId,
            forceOverwrite,
            enqueued.DeduplicatedExistingPendingJob);
    }

    private async Task<bool> HasActivityAsync(DateOnly targetDate, DateTimeOffset? fromUtc, CancellationToken ct)
    {
        await foreach (var _ in _events.QueryAsync(
                           new EventQuery(LocalDate: targetDate, FromUtc: fromUtc, Limit: 1),
                           ct))
        {
            return true;
        }

        return false;
    }

    private async Task<bool> HasActiveJobAsync(DateOnly targetDate, bool forceOverwrite, CancellationToken ct)
    {
        var jobs = await _queue.ListRecentJobsAsync(200, ct);
        return jobs.Any(job =>
            job.TargetDate == targetDate &&
            job.Status is ReportJobStatus.Pending or ReportJobStatus.Running &&
            (job.ForceOverwrite || !forceOverwrite));
    }

    private void Raise(ReportJob job, string message) =>
        ProgressChanged?.Invoke(this, new ReportProgress(job.Id, job.TargetDate, job.Status, message));
}
