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

public sealed class ModelGatewayTests
{
    [Fact]
    public async Task RedactionAndAuditDecorators_SanitizePromptAndEmbeddings_WithoutLoggingBodies()
    {
        var inner = new RecordingModelGateway();
        var audit = new InMemoryModelAuditSink();
        var gateway = new AuditedModelGateway(
            new RedactingModelGateway(inner, new SensitiveDataRedactor()),
            audit);

        var result = await gateway.CompleteAsync(
            new ModelCompletionRequest("system", "password=SuperSecret! 联系 test@example.com，今天完成架构评审"),
            CancellationToken.None);

        Assert.DoesNotContain("SuperSecret", inner.LastRequest!.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("test@example.com", inner.LastRequest.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("今天完成架构评审", inner.LastRequest.UserPrompt, StringComparison.Ordinal);
        Assert.True(result.RedactionApplied);

        _ = await gateway.EmbedAsync(
            new ModelEmbeddingRequest(["api_key=SecretToken123 普通文本"]),
            CancellationToken.None);
        Assert.DoesNotContain("SecretToken123", inner.LastEmbedding!.Inputs[0], StringComparison.Ordinal);
        Assert.Equal(2, audit.GetRecent().Count);
        Assert.All(audit.GetRecent(), entry => Assert.True(entry.Succeeded));
    }

    [Fact]
    public async Task OllamaHttpBackend_ParsesChatEmbeddingsAndProbe()
    {
        var handler = new OllamaHandler();
        var options = new ModelGatewayOptions
        {
            ChatModel = "qwen3:8b",
            EmbeddingModel = "qwen3-embedding:0.6b"
        };
        using var http = new HttpClient(handler);
        var backend = new HttpModelBackend(http, options);

        var probe = await backend.ProbeAsync(CancellationToken.None);
        Assert.True(probe.ChatAvailable);
        Assert.True(probe.EmbeddingsAvailable);

        var response = await backend.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "生成日报")],
            new ChatOptions { ModelId = "qwen3:8b" },
            CancellationToken.None);
        Assert.Equal("测试回复", response.Messages[0].Text);

        var embeddings = await backend.GenerateAsync(["活动一"], null, CancellationToken.None);
        Assert.Single(embeddings);
        Assert.Equal([0.1f, 0.2f, 0.3f], embeddings[0].Vector.ToArray());
        Assert.Contains("\"stream\":false", handler.LastChatBody, StringComparison.Ordinal);
    }

    private sealed class RecordingModelGateway : IModelGateway
    {
        public ModelCompletionRequest? LastRequest { get; private set; }
        public ModelEmbeddingRequest? LastEmbedding { get; private set; }
        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.AccessibilityText, "fake");
        public string ChatModelId => "fake-chat";
        public string EmbeddingModelId => "fake-embedding";

        public Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new ModelCompletionResult("完成", "fake-chat", ModelEndpointTier.LocalOffline, false, TimeSpan.Zero));
        }

        public Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct)
        {
            LastEmbedding = request;
            return Task.FromResult(new ModelEmbeddingResult(
                request.Inputs.Select(_ => new ReadOnlyMemory<float>([0.1f])).ToArray(),
                "fake-embedding",
                1));
        }

        public Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new ModelCapabilitySnapshot(true, true, "fake-chat", "fake-embedding", ModelEndpointTier.LocalOffline, "ok"));
    }

    private sealed class OllamaHandler : HttpMessageHandler
    {
        public string LastChatBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/tags")
            {
                return Json("""{"models":[{"name":"qwen3:8b"},{"name":"qwen3-embedding:0.6b"}]}""");
            }

            if (path == "/api/chat")
            {
                LastChatBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json("""{"message":{"role":"assistant","content":"测试回复"},"done":true}""");
            }

            if (path == "/api/embed")
            {
                return Json("""{"embeddings":[[0.1,0.2,0.3]]}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

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
        public string ChatModelId => "qwen3:8b";
        public string EmbeddingModelId => "qwen3-embedding:0.6b";

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
            Task.FromResult(new ModelEmbeddingResult([], "qwen3-embedding:0.6b", 0));

        public Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new ModelCapabilitySnapshot(true, true, "qwen3:8b", "qwen3-embedding:0.6b", ModelEndpointTier.LocalOffline, "ok"));

        private static ModelCompletionResult Result(string text) =>
            new(text, "qwen3:8b", ModelEndpointTier.LocalOffline, false, TimeSpan.Zero);
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

public sealed class EmbeddingAndBenchmarkTests
{
    [Fact]
    public async Task EmbeddingIndexer_WritesVersionedVectors_AndRebuildUsesGenerator()
    {
        var events = new FakeEventRepository(
        [
            Event("e1", "Rider", "Main.cs"),
            Event("e2", "Chrome", "Docs")
        ]);
        var model = new FakeEmbeddingModel();
        var vectors = new FakeVectorIndex();
        var indexer = new EventEmbeddingIndexer(events, vectors, model);

        var indexed = await indexer.IndexDateAsync(new DateOnly(2026, 11, 20), CancellationToken.None);
        Assert.Equal(2, indexed);
        Assert.Equal(2, vectors.Upserts.Count);
        Assert.Equal("qwen3-embedding:0.6b", vectors.Upserts[0].Model.ModelId);
        Assert.Equal(3, vectors.Upserts[0].Model.Dimensions);

        var rebuilt = await indexer.RebuildAllAsync(CancellationToken.None);
        Assert.Equal(2, rebuilt);
        Assert.NotNull(vectors.RebuiltModel);
        Assert.Equal(2, vectors.RebuiltCount);
    }

    [Fact]
    public async Task EmbeddingIndexer_TitleTextStyle_UsesStructuredDocumentTextAndVersion()
    {
        var events = new FakeEventRepository([Event("e1", "Rider", "Main.cs")]);
        var model = new FakeEmbeddingModel();
        var vectors = new FakeVectorIndex();
        var indexer = new EventEmbeddingIndexer(
            events,
            vectors,
            model,
            ModelEndpointTier.LocalOffline,
            documentStyle: "title-text");

        var indexed = await indexer.IndexDateAsync(new DateOnly(2026, 11, 20), CancellationToken.None);

        Assert.Equal(1, indexed);
        Assert.Contains(model.Inputs, input => input.StartsWith("title: Main.cs | text:", StringComparison.Ordinal));
        Assert.Equal("1.1-title-text", vectors.Upserts[0].Model.ModelVersion);
    }

    [Fact]
    public async Task BenchmarkRunner_ScoresJsonComplianceAndExportsMarkdown()
    {
        var report = await new ModelBenchmarkRunner(new BenchmarkModel()).RunAsync(CancellationToken.None);

        Assert.Equal(3, report.Cases.Count);
        Assert.Equal(1.0, report.SuccessRate);
        Assert.Equal(1.0, report.JsonComplianceRate);
        Assert.Contains("本机模型基准报告", report.ToMarkdown(), StringComparison.Ordinal);
        Assert.Contains("结构化 JSON", report.ToMarkdown(), StringComparison.Ordinal);
    }

    private static ActivityEvent Event(string id, string app, string title) =>
        new(
            id,
            OccurredTimestamp.FromInstant(new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc),
            SourceKind.WindowActivity,
            app,
            title);

    private sealed class FakeEventRepository(IReadOnlyList<ActivityEvent> events) : IEventRepository
    {
        public Task AppendBatchAsync(IReadOnlyList<ActivityEvent> items, CancellationToken ct) => Task.CompletedTask;
        public Task AppendWithMediaBatchAsync(IReadOnlyList<EventWithMediaWrite> items, CancellationToken ct) => Task.CompletedTask;

        public async IAsyncEnumerable<ActivityEvent> QueryAsync(
            EventQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            foreach (var item in events)
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

    private sealed class FakeEmbeddingModel : IModelGateway
    {
        public List<string> Inputs { get; } = [];

        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.ModelGateway, "fake");
        public string ChatModelId => "fake-chat";
        public string EmbeddingModelId => "qwen3-embedding:0.6b";

        public Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct) =>
            Task.FromResult(new ModelCompletionResult("{}", ChatModelId, ModelEndpointTier.LocalOffline, false, TimeSpan.Zero));

        public Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct)
        {
            Inputs.AddRange(request.Inputs);
            return Task.FromResult(new ModelEmbeddingResult(
                request.Inputs.Select((_, index) => new ReadOnlyMemory<float>([0.1f + index, 0.2f, 0.3f])).ToArray(),
                EmbeddingModelId,
                3));
        }

        public Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new ModelCapabilitySnapshot(true, true, ChatModelId, EmbeddingModelId, ModelEndpointTier.LocalOffline, "ok"));
    }

    private sealed class FakeVectorIndex : IVectorIndex
    {
        public List<(string EventId, VectorModelDescriptor Model)> Upserts { get; } = [];
        public VectorModelDescriptor? RebuiltModel { get; private set; }
        public int RebuiltCount { get; private set; }

        public Task<VectorIndexMetadata> GetMetadataAsync(CancellationToken ct) =>
            Task.FromResult(new VectorIndexMetadata("fake", false, null, Upserts.Count));

        public Task UpsertAsync(string eventId, ReadOnlyMemory<float> embedding, VectorModelDescriptor model, CancellationToken ct)
        {
            Upserts.Add((eventId, model));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(ReadOnlyMemory<float> queryEmbedding, int topK, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VectorSearchHit>>([]);

        public async Task RebuildAsync(
            VectorModelDescriptor targetModel,
            Func<CancellationToken, IAsyncEnumerable<(string EventId, ReadOnlyMemory<float> Embedding)>> generator,
            CancellationToken ct)
        {
            RebuiltModel = targetModel;
            RebuiltCount = 0;
            await foreach (var _ in generator(ct))
            {
                RebuiltCount++;
            }
        }
    }

    private sealed class BenchmarkModel : IModelGateway
    {
        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.ModelGateway, "fake");
        public string ChatModelId => "qwen3:8b";
        public string EmbeddingModelId => "qwen3-embedding:0.6b";

        public Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct)
        {
            var text = request.UserPrompt.Contains("只输出 JSON", StringComparison.Ordinal)
                ? """
                  ```json
                  {"overview":"总结","highlights":["成果"],"standup":{"today":"计划"}}
                  ```
                  """
                : "完成代码评审与数据库迁移。";
            return Task.FromResult(new ModelCompletionResult(text, ChatModelId, ModelEndpointTier.LocalOffline, false, TimeSpan.FromMilliseconds(5)));
        }

        public Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct) =>
            Task.FromResult(new ModelEmbeddingResult([], EmbeddingModelId, 0));

        public Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new ModelCapabilitySnapshot(true, true, ChatModelId, EmbeddingModelId, ModelEndpointTier.LocalOffline, "ok"));
    }
}

public sealed class ReportWorkerTests
{
    [Fact]
    public async Task Worker_StartupCatchUp_AndQueuedJob_AreProcessedBySingleConsumer()
    {
        var queue = new FakeReportQueue();
        var runner = new FakeRunner();
        var reports = new InMemoryReportRepository();
        // Fixed at 09:00 local so the "today after 18:30" branch never fires; the test asserts only the catch-up job.
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        var worker = new ReportJobWorker(
            queue,
            runner,
            reports,
            clock,
            new ReportScheduleOptions
            {
                EnableScheduledDaily = false,
                EnableStartupCatchUp = true,
                PollInterval = TimeSpan.FromMilliseconds(10)
            },
            NullLogger<ReportJobWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => queue.Jobs.Count > 0);
        await WaitUntilAsync(() => runner.Calls > 0 && queue.Jobs.All(job => job.Status is ReportJobStatus.Completed or ReportJobStatus.SkippedAlreadyExists));
        await worker.DisposeAsync();

        Assert.Equal(1, runner.Calls);
        Assert.Equal(new DateOnly(2026, 11, 19), queue.Jobs[0].TargetDate);
        Assert.Equal(ReportTriggerSource.StartupCatchUp, queue.Jobs[0].TriggerSource);
    }

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
                var existing = Jobs.FirstOrDefault(job => job.TargetDate == request.TargetDate &&
                                                          job.Status == ReportJobStatus.Pending);
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

    private sealed class InMemoryReportRepository : IReportRepository
    {
        public Task<DailyReport?> GetLatestAsync(DateOnly localDate, CancellationToken ct) => Task.FromResult<DailyReport?>(null);
        public Task<IReadOnlyList<DailyReport>> ListVersionsAsync(DateOnly localDate, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DailyReport>>([]);
        public Task<DailyReport> AppendAsync(
            DateOnly localDate,
            string markdown,
            string statsJson,
            string modelId,
            ModelEndpointTier endpointTier,
            bool redactionApplied,
            ReportTriggerSource triggerSource,
            DateTimeOffset generatedAtUtc,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
