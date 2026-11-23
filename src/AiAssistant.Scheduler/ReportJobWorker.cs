using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Scheduler;

public sealed class ReportScheduleOptions
{
    public TimeOnly DailyLocalTime { get; init; } = new(18, 30);
    public bool EnableScheduledDaily { get; init; } = true;
    public bool EnableStartupCatchUp { get; init; } = true;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
}

public sealed record ReportProgress(
    string JobId,
    DateOnly TargetDate,
    ReportJobStatus Status,
    string Message);

/// <summary>
/// Single-consumer persistent report worker. Manual, startup catch-up and scheduled triggers all enter the same
/// idempotent report_jobs queue (ADR-0010).
/// </summary>
public sealed class ReportJobWorker : IAsyncDisposable
{
    private readonly IReportJobQueue _queue;
    private readonly IReportGenerationRunner _runner;
    private readonly IReportRepository _reports;
    private readonly IClock _clock;
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
        IClock clock,
        ReportScheduleOptions options,
        ILogger<ReportJobWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _queue = queue;
        _runner = runner;
        _reports = reports;
        _clock = clock;
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
            await EnqueueStartupCatchUpAsync(ct);
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
                await EnqueueScheduledIfDueAsync(ct);
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

    private async Task EnqueueStartupCatchUpAsync(CancellationToken ct)
    {
        var now = _clock.Now();
        var yesterday = now.LocalDate.AddDays(-1);
        if (await _reports.GetLatestAsync(yesterday, ct) is null)
        {
            await _queue.EnqueueAsync(
                new ReportJobRequest(yesterday, ReportTriggerSource.StartupCatchUp),
                ct);
        }

        if (TimeOnly.FromDateTime(now.ToCapturedLocalTime().DateTime) >= _options.DailyLocalTime &&
            await _reports.GetLatestAsync(now.LocalDate, ct) is null)
        {
            await _queue.EnqueueAsync(
                new ReportJobRequest(now.LocalDate, ReportTriggerSource.StartupCatchUp),
                ct);
        }
    }

    private async Task EnqueueScheduledIfDueAsync(CancellationToken ct)
    {
        if (!_options.EnableScheduledDaily)
        {
            return;
        }

        var now = _clock.Now();
        var localTime = TimeOnly.FromDateTime(now.ToCapturedLocalTime().DateTime);
        if (localTime < _options.DailyLocalTime || _lastScheduledDate == now.LocalDate)
        {
            return;
        }

        _lastScheduledDate = now.LocalDate;
        await _queue.EnqueueAsync(
            new ReportJobRequest(now.LocalDate, ReportTriggerSource.ScheduledDaily),
            ct);
    }

    private void Raise(ReportJob job, string message) =>
        ProgressChanged?.Invoke(this, new ReportProgress(job.Id, job.TargetDate, job.Status, message));
}
