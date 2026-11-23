using System.Net;
using System.Text;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiAssistant.Models.Tests;

[Trait("Category", "Unit")]
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
        Assert.Equal("embeddinggemma-2:740m", vectors.Upserts[0].Model.ModelId);
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
    public async Task BenchmarkRunner_UsesConfiguredTier()
    {
        var model = new BenchmarkModel();
        var runner = new ModelBenchmarkRunner(model, ModelEndpointTier.CorporateIntranet);

        var report = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(ModelEndpointTier.CorporateIntranet, model.LastTier);
        Assert.Equal(model.ChatModelId, report.ChatModel);
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
        public string EmbeddingModelId => "embeddinggemma-2:740m";

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
        public ModelEndpointTier? LastTier { get; private set; }

        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.ModelGateway, "fake");
        public string ChatModelId => "gemma4:12b";
        public string EmbeddingModelId => "embeddinggemma-2:740m";

        public Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct)
        {
            LastTier = request.Tier;
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

