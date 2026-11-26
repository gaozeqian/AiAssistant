using System.Net;
using System.Text;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Models;
using AiAssistant.Processing;
using AiAssistant.Scheduler;
using AiAssistant.Summarization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiAssistant.Report.Tests;

[Trait("Category", "Unit")]
public sealed class ReportGenerationTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 11, 20, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Date = new(2026, 11, 20);

    [Fact]
    public void ActivitySegmenter_SplitsByInactivityGap_AndCountsApplications()
    {
        var events = new List<ActivityEvent>
        {
            Event("e1", BaseUtc, "Rider", "Main.cs"),
            Event("e2", BaseUtc.AddMinutes(3), "Rider", "Main.cs"),
            Event("e3", BaseUtc.AddMinutes(45), "Chrome", "Docs")
        };
        var segments = new ActivitySegmenter(TimeSpan.FromMinutes(12)).Segment(events);

        Assert.Equal(2, segments.Count);
        Assert.Equal(2, segments[0].Events.Count);
        Assert.Equal(2, segments[0].AppCounts["Rider"]);
        Assert.Equal(1, segments[1].AppCounts["Chrome"]);
    }

    [Fact]
    public void ReportJsonSchema_AcceptsFencedJson_AndRejectsIncompleteSchema()
    {
        const string valid = """
            ```json
            {"overview":"完成日报闭环","highlights":["接入模型"],"projects":[{"name":"AiAssistant","summary":"S5","durationMinutes":60}],"timeline":[{"time":"09:00","activity":"开发"}],"standup":{"yesterday":"S4","today":"S5","blockers":[]}}
            ```
            """;
        Assert.True(ReportJsonSchema.TryParse(valid, out var document, out _));
        Assert.Equal("完成日报闭环", document!.Overview);
        Assert.Equal("S5", document.Standup.Today);

        // The prompt tells the model to use empty strings/arrays for plans it cannot infer from
        // activity data, so an empty standup must not be treated as a schema violation
        // (regression: real local models returned an empty `today` and every retry was rejected).
        const string emptyStandup = """
            {"overview":"完成日报闭环","highlights":["接入模型"],"timeline":[{"time":"09:00","activity":"开发"}],"standup":{"yesterday":"","today":"","blockers":[]}}
            """;
        Assert.True(ReportJsonSchema.TryParse(emptyStandup, out var emptyDocument, out _));
        Assert.Equal(string.Empty, emptyDocument!.Standup.Today);

        Assert.False(ReportJsonSchema.TryParse("""{"overview":"x"}""", out _, out var error));
        Assert.Equal("schema-violation", error);
        Assert.False(ReportJsonSchema.TryParse("not json", out _, out error));
        Assert.Equal("no-json-object", error);
    }

    [Fact]
    public async Task Generator_MapReduceRetriesInvalidJson_AndCreatesVersionedReportsIdempotently()
    {
        var events = new InMemoryEventRepository(
        [
            Event("e1", BaseUtc, "Rider", "Main.cs", "实现模型网关"),
            Event("e2", BaseUtc.AddMinutes(5), "Rider", "Tests.cs", "编写测试")
        ]);
        var model = new ScriptedModelGateway();
        var reports = new InMemoryReportRepository();
        var runner = CreateRunner(events, model, reports);

        var first = await runner.GenerateAsync(new ReportJobRequest(Date, ReportTriggerSource.Manual), CancellationToken.None);
        Assert.True(first.Succeeded);
        Assert.Equal(1, first.Report!.Version);
        Assert.Contains("完成日报闭环", first.Report.Markdown, StringComparison.Ordinal);
        Assert.Contains("站会视图", first.Report.Markdown, StringComparison.Ordinal);
        Assert.Equal(2, model.ReduceCalls); // invalid first attempt + valid retry
        Assert.True(model.MapCalls >= 1);

        var skipped = await runner.GenerateAsync(new ReportJobRequest(Date, ReportTriggerSource.ScheduledDaily), CancellationToken.None);
        Assert.True(skipped.SkippedExisting);
        Assert.Equal(1, skipped.Report!.Version);
        Assert.Equal(2, model.ReduceCalls);

        var forced = await runner.GenerateAsync(
            new ReportJobRequest(Date, ReportTriggerSource.Manual, ForceOverwrite: true),
            CancellationToken.None);
        Assert.True(forced.Succeeded);
        Assert.Equal(2, forced.Report!.Version);
    }

    [Fact]
    public async Task Generator_AllInvalidModelOutputs_FailWithoutPersistingEmptyReport()
    {
        var events = new InMemoryEventRepository([Event("e1", BaseUtc, "Rider", "Main.cs", "工作")]);
        var model = new ScriptedModelGateway { AlwaysInvalidReduce = true };
        var reports = new InMemoryReportRepository();
        var runner = CreateRunner(events, model, reports);

        var outcome = await runner.GenerateAsync(new ReportJobRequest(Date, ReportTriggerSource.Manual), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Report);
        Assert.NotNull(outcome.FailureReason);
        Assert.Empty(await reports.ListVersionsAsync(Date, CancellationToken.None));
    }

    private static ReportGenerationRunner CreateRunner(
        IEventRepository events,
        IModelGateway model,
        IReportRepository reports) =>
        new(
            events,
            model,
            reports,
            new SystemClock(),
            new SensitiveDataRedactor(),
            new ReportGenerationOptions { MaxStructuredRetries = 1 },
            NullLogger<ReportGenerationRunner>.Instance);

    private static ActivityEvent Event(string id, DateTimeOffset at, string app, string title, string? text = null) =>
        new(
            id,
            OccurredTimestamp.FromInstant(at, TimeZoneInfo.Utc),
            SourceKind.WindowActivity,
            app,
            title,
            extractedText: text);

    private sealed class ScriptedModelGateway : IModelGateway
    {
        public int MapCalls { get; private set; }
        public int ReduceCalls { get; private set; }
        public bool AlwaysInvalidReduce { get; init; }
        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.AccessibilityText, "fake model");
        public string ChatModelId => "gemma4:12b";
        public string EmbeddingModelId => "embeddinggemma-2:740m";

        public Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct)
        {
            if (request.Purpose == "report-map")
            {
                MapCalls++;
                return Task.FromResult(Result("完成模型网关与日报面板开发。"));
            }

            ReduceCalls++;
            var invalid = AlwaysInvalidReduce || ReduceCalls == 1;
            var text = invalid
                ? "这不是 JSON"
                : """
                  {"overview":"完成日报闭环","highlights":["接入模型网关","完成版本化日报"],"projects":[{"name":"AiAssistant","summary":"S5","durationMinutes":120}],"timeline":[{"time":"09:00","activity":"开发"}],"standup":{"yesterday":"S4","today":"S5 联调","blockers":[]}}
                  """;
            return Task.FromResult(Result(text));
        }

        public Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct) =>
            Task.FromResult(new ModelEmbeddingResult([], "embeddinggemma-2:740m", 0));

        public Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new ModelCapabilitySnapshot(true, true, "gemma4:12b", "embeddinggemma-2:740m", ModelEndpointTier.LocalOffline, "ok"));

        private static ModelCompletionResult Result(string text) =>
            new(text, "gemma4:12b", ModelEndpointTier.LocalOffline, false, TimeSpan.Zero);
    }

    private sealed class InMemoryEventRepository(IReadOnlyList<ActivityEvent> events) : IEventRepository
    {
        public Task AppendBatchAsync(IReadOnlyList<ActivityEvent> items, CancellationToken ct) => Task.CompletedTask;
        public Task AppendWithMediaBatchAsync(IReadOnlyList<EventWithMediaWrite> items, CancellationToken ct) => Task.CompletedTask;

        public async IAsyncEnumerable<ActivityEvent> QueryAsync(
            EventQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            foreach (var item in events.Where(item => query.LocalDate is null || item.Timestamp.LocalDate == query.LocalDate))
            {
                yield return item;
            }
        }

        public Task<ActivityEvent?> GetByIdAsync(string eventId, CancellationToken ct) =>
            Task.FromResult(events.FirstOrDefault(item => item.Id == eventId));
        public Task<IReadOnlyList<DateOnly>> ListActiveDatesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DateOnly>>(events.Select(item => item.Timestamp.LocalDate).Distinct().ToArray());
        public Task<bool> DeleteEventAsync(string eventId, CancellationToken ct) => Task.FromResult(false);
        public Task<int> PurgeDateAsync(DateOnly localDate, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class InMemoryReportRepository : IReportRepository
    {
        private readonly List<DailyReport> _reports = [];

        public Task<DailyReport?> GetLatestAsync(DateOnly localDate, CancellationToken ct) =>
            Task.FromResult(_reports.Where(report => report.LocalDate == localDate)
                .OrderByDescending(report => report.Version)
                .FirstOrDefault());

        public Task<IReadOnlyList<DailyReport>> ListVersionsAsync(DateOnly localDate, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DailyReport>>(_reports.Where(report => report.LocalDate == localDate)
                .OrderByDescending(report => report.Version)
                .ToArray());

        public Task<DailyReport> AppendAsync(
            DateOnly localDate,
            string markdown,
            string statsJson,
            string modelId,
            ModelEndpointTier endpointTier,
            bool redactionApplied,
            ReportTriggerSource triggerSource,
            DateTimeOffset generatedAtUtc,
            CancellationToken ct)
        {
            var version = _reports.Where(report => report.LocalDate == localDate)
                .Select(report => report.Version)
                .DefaultIfEmpty(0)
                .Max() + 1;
            var report = new DailyReport(
                $"rpt_{version}",
                localDate,
                version,
                markdown,
                statsJson,
                modelId,
                endpointTier,
                redactionApplied,
                triggerSource,
                generatedAtUtc);
            _reports.Add(report);
            return Task.FromResult(report);
        }
    }
}

[Trait("Category", "Unit")]
public sealed class ReportWorkerTests
{
    private static readonly DateOnly TargetDate = new(2026, 11, 19);
    private static readonly DateTimeOffset LocalMorning = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Worker_Startup_GeneratesPreviousDayReport_WhenActivityExists()
    {
        var queue = new FakeReportQueue();
        var runner = new FakeRunner();
        var reports = new InMemoryReportRepository();
        var events = new FakeEventRepository(
            [Event("e1", new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero))]);
        var worker = CreateWorker(queue, runner, reports, events, new FixedClock(LocalMorning));

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => queue.Jobs.Count > 0 && runner.Calls > 0 &&
            queue.Jobs.All(job => job.Status is ReportJobStatus.Completed or ReportJobStatus.SkippedAlreadyExists));
        await worker.DisposeAsync();

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(TargetDate, job.TargetDate);
        Assert.Equal(ReportTriggerSource.StartupCatchUp, job.TriggerSource);
        Assert.False(job.ForceOverwrite);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task Worker_Startup_ChecksPreviousDayBeforeConfiguredTime()
    {
        var queue = new FakeReportQueue();
        var runner = new FakeRunner();
        var events = new FakeEventRepository(
            [Event("e1", new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero))]);
        // 00:10 is before the 00:30 scheduled time, so only the startup check can enqueue.
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 20, 0, 10, 0, TimeSpan.Zero));
        var worker = CreateWorker(queue, runner, new InMemoryReportRepository(), events, clock);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => queue.Jobs.Count > 0 && runner.Calls > 0);
        await worker.DisposeAsync();

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(TargetDate, job.TargetDate);
        Assert.Equal(ReportTriggerSource.StartupCatchUp, job.TriggerSource);
    }

    [Fact]
    public async Task Worker_Startup_SkipsReport_WhenDateHasNoActivity()
    {
        var queue = new FakeReportQueue();
        var runner = new FakeRunner();
        var worker = CreateWorker(
            queue,
            runner,
            new InMemoryReportRepository(),
            new FakeEventRepository([]),
            new FixedClock(LocalMorning));

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await worker.DisposeAsync();

        Assert.Empty(queue.Jobs);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Worker_Startup_Regenerates_WhenActivityArrivedAfterLatestReport()
    {
        var reportTime = new DateTimeOffset(2026, 11, 19, 18, 0, 0, TimeSpan.Zero);
        var queue = new FakeReportQueue();
        var runner = new FakeRunner();
        var reports = new InMemoryReportRepository([Report(TargetDate, reportTime)]);
        var events = new FakeEventRepository(
        [
            Event("e1", reportTime.AddHours(-2)),
            Event("e2", reportTime.AddHours(2))
        ]);
        var worker = CreateWorker(queue, runner, reports, events, new FixedClock(LocalMorning));

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => queue.Jobs.Count > 0 && runner.Calls > 0 &&
            queue.Jobs.All(job => job.Status is ReportJobStatus.Completed or ReportJobStatus.SkippedAlreadyExists));
        await worker.DisposeAsync();

        var job = Assert.Single(queue.Jobs);
        Assert.True(job.ForceOverwrite);
        Assert.Equal(ReportTriggerSource.StartupCatchUp, job.TriggerSource);
    }

    [Fact]
    public async Task Worker_Startup_SkipsRegeneration_WhenNoActivityAfterLatestReport()
    {
        var reportTime = new DateTimeOffset(2026, 11, 19, 21, 0, 0, TimeSpan.Zero);
        var queue = new FakeReportQueue();
        var runner = new FakeRunner();
        var reports = new InMemoryReportRepository([Report(TargetDate, reportTime)]);
        var events = new FakeEventRepository([Event("e1", reportTime.AddHours(-1))]);
        var worker = CreateWorker(queue, runner, reports, events, new FixedClock(LocalMorning));

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await worker.DisposeAsync();

        Assert.Empty(queue.Jobs);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Worker_ScheduledCheck_TargetsPreviousDayAtConfiguredTime()
    {
        var queue = new FakeReportQueue();
        var runner = new FakeRunner();
        var events = new FakeEventRepository(
            [Event("e1", new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero))]);
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 20, 0, 30, 0, TimeSpan.Zero));
        var worker = CreateWorker(
            queue,
            runner,
            new InMemoryReportRepository(),
            events,
            clock,
            new ReportScheduleOptions
            {
                EnableStartupCatchUp = false,
                PollInterval = TimeSpan.FromMilliseconds(10)
            });

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => queue.Jobs.Count > 0 && runner.Calls > 0);
        await worker.DisposeAsync();

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(TargetDate, job.TargetDate);
        Assert.Equal(ReportTriggerSource.ScheduledDaily, job.TriggerSource);
    }

    [Fact]
    public async Task Worker_AutomaticChecks_SkipWhenDisabled()
    {
        var queue = new FakeReportQueue();
        var runner = new FakeRunner();
        var events = new FakeEventRepository(
            [Event("e1", new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero))]);
        var worker = CreateWorker(
            queue,
            runner,
            new InMemoryReportRepository(),
            events,
            new FixedClock(LocalMorning),
            settings: new ReportScheduleSettings(new TimeOnly(0, 30), Enabled: false));

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await worker.DisposeAsync();

        Assert.Empty(queue.Jobs);
        Assert.Equal(0, runner.Calls);
    }

    private static ReportJobWorker CreateWorker(
        FakeReportQueue queue,
        FakeRunner runner,
        InMemoryReportRepository reports,
        FakeEventRepository events,
        FixedClock clock,
        ReportScheduleOptions? options = null,
        ReportScheduleSettings? settings = null) =>
        new(
            queue,
            runner,
            reports,
            events,
            clock,
            new FakeScheduleSettings(settings ?? ReportScheduleSettings.Default),
            options ?? new ReportScheduleOptions { PollInterval = TimeSpan.FromMilliseconds(10) },
            NullLogger<ReportJobWorker>.Instance);

    private static ActivityEvent Event(string id, DateTimeOffset occurredAtUtc) =>
        new(
            id,
            OccurredTimestamp.FromInstant(occurredAtUtc, TimeZoneInfo.Utc),
            SourceKind.WindowActivity,
            "Rider",
            "Main.cs");

    private static DailyReport Report(DateOnly date, DateTimeOffset generatedAtUtc) =>
        new(
            $"rpt_{date:yyyyMMdd}",
            date,
            1,
            "# report",
            "{}",
            "fake",
            ModelEndpointTier.LocalOffline,
            false,
            ReportTriggerSource.Manual,
            generatedAtUtc);

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FakeReportQueue : IReportJobQueue
    {
        private readonly Lock _sync = new();
        public List<ReportJob> Jobs { get; } = [];

        public Task<EnqueueResult> EnqueueAsync(ReportJobRequest request, CancellationToken ct)
        {
            lock (_sync)
            {
                var existing = !request.ForceOverwrite
                    ? Jobs.FirstOrDefault(job => job.TargetDate == request.TargetDate &&
                                                 job.Status is ReportJobStatus.Pending or ReportJobStatus.Running)
                    : null;
                if (existing is not null)
                {
                    return Task.FromResult(new EnqueueResult(existing.Id, true));
                }

                var job = new ReportJob(
                    $"job_{Jobs.Count + 1}",
                    request.TargetDate,
                    request.TriggerSource,
                    request.ForceOverwrite,
                    DateTimeOffset.UtcNow);
                Jobs.Add(job);
                return Task.FromResult(new EnqueueResult(job.Id, false));
            }
        }

        public Task<int> RecoverInterruptedJobsAsync(CancellationToken ct) => Task.FromResult(0);

        public Task<ReportJob?> ClaimNextPendingAsync(CancellationToken ct)
        {
            lock (_sync)
            {
                var job = Jobs.FirstOrDefault(item => item.Status == ReportJobStatus.Pending);
                job?.MarkRunning(DateTimeOffset.UtcNow);
                return Task.FromResult(job);
            }
        }

        public Task UpdateAsync(ReportJob job, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ReportJob>> ListRecentJobsAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReportJob>>(Jobs.TakeLast(limit).ToArray());
    }

    private sealed class FakeScheduleSettings(ReportScheduleSettings settings) : IReportScheduleSettingsStore
    {
        public ReportScheduleSettings Settings { get; private set; } = settings;

        public ReportScheduleSettings Load() => Settings;

        public Task SaveAsync(ReportScheduleSettings settings, CancellationToken ct)
        {
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEventRepository(IReadOnlyList<ActivityEvent> events) : IEventRepository
    {
        public Task AppendBatchAsync(IReadOnlyList<ActivityEvent> items, CancellationToken ct) => Task.CompletedTask;

        public Task AppendWithMediaBatchAsync(IReadOnlyList<EventWithMediaWrite> items, CancellationToken ct) =>
            Task.CompletedTask;

        public async IAsyncEnumerable<ActivityEvent> QueryAsync(
            EventQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            var matches = events.Where(item => query.LocalDate is null || item.Timestamp.LocalDate == query.LocalDate);
            if (query.FromUtc is { } fromUtc)
            {
                matches = matches.Where(item => item.Timestamp.OccurredAtUtc >= fromUtc);
            }

            foreach (var item in matches.Take(query.Limit))
            {
                yield return item;
            }
        }

        public Task<ActivityEvent?> GetByIdAsync(string eventId, CancellationToken ct) =>
            Task.FromResult(events.FirstOrDefault(item => item.Id == eventId));

        public Task<IReadOnlyList<DateOnly>> ListActiveDatesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DateOnly>>(events.Select(item => item.Timestamp.LocalDate).Distinct().ToArray());

        public Task<bool> DeleteEventAsync(string eventId, CancellationToken ct) => Task.FromResult(false);

        public Task<int> PurgeDateAsync(DateOnly localDate, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class FakeRunner : IReportGenerationRunner
    {
        private int _calls;
        public int Calls => _calls;

        public Task<ReportGenerationOutcome> GenerateAsync(ReportJobRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            var report = new DailyReport(
                "rpt_1",
                request.TargetDate,
                1,
                "# report",
                "{}",
                "fake",
                ModelEndpointTier.LocalOffline,
                false,
                request.TriggerSource,
                DateTimeOffset.UtcNow);
            return Task.FromResult(new ReportGenerationOutcome(report, false, null));
        }
    }

    private sealed class InMemoryReportRepository(IEnumerable<DailyReport>? initial = null) : IReportRepository
    {
        private readonly List<DailyReport> _reports = initial?.ToList() ?? [];

        public Task<DailyReport?> GetLatestAsync(DateOnly localDate, CancellationToken ct) =>
            Task.FromResult(_reports.Where(report => report.LocalDate == localDate)
                .OrderByDescending(report => report.Version)
                .FirstOrDefault());

        public Task<IReadOnlyList<DailyReport>> ListVersionsAsync(DateOnly localDate, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DailyReport>>(_reports.Where(report => report.LocalDate == localDate)
                .OrderByDescending(report => report.Version)
                .ToArray());

        public Task<DailyReport> AppendAsync(
            DateOnly localDate,
            string markdown,
            string statsJson,
            string modelId,
            ModelEndpointTier endpointTier,
            bool redactionApplied,
            ReportTriggerSource triggerSource,
            DateTimeOffset generatedAtUtc,
            CancellationToken ct)
        {
            var version = _reports.Where(report => report.LocalDate == localDate)
                .Select(report => report.Version)
                .DefaultIfEmpty(0)
                .Max() + 1;
            var report = new DailyReport(
                $"rpt_{version}",
                localDate,
                version,
                markdown,
                statsJson,
                modelId,
                endpointTier,
                redactionApplied,
                triggerSource,
                generatedAtUtc);
            _reports.Add(report);
            return Task.FromResult(report);
        }
    }
}
