using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.LocalApi;
using AiAssistant.Testing.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiAssistant.LocalApi.Tests;

internal static class TestDirectories
{
    public static string Create(string prefix)
    {
        return TempArtifactDirectory.CreatePath(prefix);
    }
}

internal sealed class FakeReportRasterizer : IReportRasterizer
{
    public byte[] RenderPng(RangeReport report) => [0x89, 0x50, 0x4E, 0x47];

    public byte[] RenderPdf(RangeReport report) => "%PDF-fake"u8.ToArray();
}

internal sealed class FakeRangeReportService : IRangeReportService
{
    public Task<RangeReport> GenerateAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct) =>
        Task.FromResult(new RangeReport(fromDate, toDate, "# report", "{}", [], "none", true, "no-evidence"));
}

internal sealed class QueryableEventRepository(IReadOnlyList<ActivityEvent> events) : IEventRepository
{
    public Task AppendBatchAsync(IReadOnlyList<ActivityEvent> items, CancellationToken ct) => Task.CompletedTask;

    public Task AppendWithMediaBatchAsync(IReadOnlyList<EventWithMediaWrite> items, CancellationToken ct) => Task.CompletedTask;

    public async IAsyncEnumerable<ActivityEvent> QueryAsync(
        EventQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        IEnumerable<ActivityEvent> filtered = events;
        if (query.LocalDate is { } date)
        {
            filtered = filtered.Where(item => item.Timestamp.LocalDate == date);
        }

        if (query.FromLocalDate is { } from)
        {
            filtered = filtered.Where(item => item.Timestamp.LocalDate >= from);
        }

        if (query.ToLocalDate is { } to)
        {
            filtered = filtered.Where(item => item.Timestamp.LocalDate <= to);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchKeyword))
        {
            var keyword = query.SearchKeyword;
            filtered = filtered.Where(item =>
                item.WindowTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (item.ExtractedText?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Summary?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        foreach (var item in filtered.Take(query.Limit))
        {
            ct.ThrowIfCancellationRequested();
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

internal sealed class InMemoryReportRepository : IReportRepository
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

internal sealed class ScriptedModelGateway(string completion, bool operational = true) : IModelGateway
{
    public int CompleteCalls { get; private set; }

    public ModelEndpointTier LastRequestedTier { get; private set; }

    public CapabilityState Capability => operational
        ? CapabilityState.Supported(CapabilityKind.ModelGateway, "fake model")
        : CapabilityState.Unavailable(CapabilityKind.ModelGateway, "offline");

    public string ChatModelId => "fake-chat";

    public string EmbeddingModelId => "fake-embedding";

    public Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct)
    {
        CompleteCalls++;
        LastRequestedTier = request.Tier;
        return Task.FromResult(new ModelCompletionResult(
            completion,
            ChatModelId,
            request.Tier,
            false,
            TimeSpan.Zero));
    }

    public Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct) =>
        Task.FromResult(new ModelEmbeddingResult(
            request.Inputs.Select(_ => new ReadOnlyMemory<float>([0.1f, 0.2f, 0.3f])).ToArray(),
            EmbeddingModelId,
            3));

    public Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct) =>
        Task.FromResult(new ModelCapabilitySnapshot(
            true,
            true,
            ChatModelId,
            EmbeddingModelId,
            ModelEndpointTier.LocalOffline,
            "ok"));
}

internal sealed class FixedVectorIndex(IReadOnlyList<VectorSearchHit> hits) : IVectorIndex
{
    public Task<VectorIndexMetadata> GetMetadataAsync(CancellationToken ct) =>
        Task.FromResult(new VectorIndexMetadata("fake", false, null, hits.Count));

    public Task UpsertAsync(string eventId, ReadOnlyMemory<float> embedding, VectorModelDescriptor model, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VectorSearchHit>>(hits.Take(topK).ToArray());

    public async Task RebuildAsync(
        VectorModelDescriptor targetModel,
        Func<CancellationToken, IAsyncEnumerable<(string EventId, ReadOnlyMemory<float> Embedding)>> generator,
        CancellationToken ct)
    {
        await foreach (var _ in generator(ct))
        {
        }
    }
}

internal sealed class FakeKeyStore : IKeyStore
{
    private readonly byte[] _key = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();

    public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.KeyStore, "fake key store");

    public Task<byte[]> GetOrCreateMasterKeyAsync(string keyId, CancellationToken ct) => Task.FromResult(_key.ToArray());

    public Task RotateWrapperKeyAsync(string keyId, CancellationToken ct) => Task.CompletedTask;

    public Task ImportMasterKeyAsync(string keyId, ReadOnlyMemory<byte> masterKey, CancellationToken ct) =>
        Task.CompletedTask;

    public IKeyStore CreateForDirectory(string dataDirectory) => this;
}
