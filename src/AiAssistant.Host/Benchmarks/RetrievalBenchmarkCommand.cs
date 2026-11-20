using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Summarization;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiAssistant.Host.Benchmarks;

/// <summary>
/// Runs the synthetic retrieval corpus through the configured embedding model and compares keyword-only
/// against hybrid RRF retrieval. Writes a Markdown report; no user data is read or written.
/// </summary>
public sealed class RetrievalBenchmarkCommand(IModelGateway model)
{
    private const int BatchSize = 16;
    private const int TopK = 8;

    private string DocumentStyle => EmbeddingProfile.ResolveDocumentStyle(
        model.EmbeddingModelId,
        Environment.GetEnvironmentVariable("AIASSISTANT_EMBEDDING_DOCUMENT_STYLE"));

    private string QueryPrefix => EmbeddingProfile.ResolveQueryPrefix(
        model.EmbeddingModelId,
        Environment.GetEnvironmentVariable("AIASSISTANT_EMBEDDING_QUERY_PREFIX"));

    public async Task<int> RunAsync(string outputPath, CancellationToken ct)
    {
        try
        {
            var events = RetrievalBenchmarkCorpus.CreateEvents();
            var queries = RetrievalBenchmarkCorpus.CreateQueries();
            var repository = new InMemoryBenchmarkRepository(events);
            var index = new InMemoryVectorIndex();
            var vectors = new List<ReadOnlyMemory<float>>(events.Count);

            for (var offset = 0; offset < events.Count; offset += BatchSize)
            {
                var batch = events
                    .Skip(offset)
                    .Take(BatchSize)
                    .Select(BuildDocumentText)
                    .ToArray();
                var embedding = await model.EmbedAsync(
                    new ModelEmbeddingRequest(batch, Purpose: "retrieval-benchmark"),
                    ct);
                if (embedding.Vectors.Count != batch.Length)
                {
                    Console.Error.WriteLine(
                        $"Embedding endpoint returned {embedding.Vectors.Count} vectors for {batch.Length} inputs.");
                    return 2;
                }

                vectors.AddRange(embedding.Vectors);
            }

            var dimensions = vectors.Count == 0 ? 0 : vectors[0].Length;
            if (dimensions == 0)
            {
                Console.Error.WriteLine("Embedding endpoint returned zero-dimension vectors.");
                return 2;
            }

            var descriptor = new VectorModelDescriptor(model.EmbeddingModelId, "retrieval-benchmark", dimensions);
            for (var indexPosition = 0; indexPosition < events.Count; indexPosition++)
            {
                index.Upsert(events[indexPosition].Id, vectors[indexPosition], descriptor);
            }

            var keywordSearch = new HybridSearchService(
                repository,
                index,
                model,
                new HybridSearchOptions { EnableVectorRecall = false, TopK = TopK },
                NullLogger<HybridSearchService>.Instance);
            var hybridSearch = new HybridSearchService(
                repository,
                index,
                model,
                new HybridSearchOptions
                {
                    EnableVectorRecall = true,
                    TopK = TopK,
                    EmbeddingQueryPrefix = QueryPrefix
                },
                NullLogger<HybridSearchService>.Instance);

            var keyword = await RetrievalBenchmarkEvaluator.EvaluateAsync(
                keywordSearch,
                queries,
                RetrievalBenchmarkCorpus.FromDate,
                RetrievalBenchmarkCorpus.ToDate,
                "关键词-only",
                TopK,
                ct);
            var hybrid = await RetrievalBenchmarkEvaluator.EvaluateAsync(
                hybridSearch,
                queries,
                RetrievalBenchmarkCorpus.FromDate,
                RetrievalBenchmarkCorpus.ToDate,
                "关键词 + 真实 embedding RRF",
                TopK,
                ct);

            var markdown = BuildMarkdown(model, dimensions, events.Count, queries.Count, keyword, hybrid);
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(outputPath, markdown, ct);
            Console.WriteLine(
                $"Retrieval benchmark: model={model.EmbeddingModelId} dim={dimensions} " +
                $"keywordRecall={keyword.RecallAtK:P1} keywordMrr={keyword.Mrr:F3} " +
                $"hybridRecall={hybrid.RecallAtK:P1} hybridMrr={hybrid.Mrr:F3}");
            Console.WriteLine($"report={Path.GetFullPath(outputPath)}");
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Retrieval benchmark failed ({ex.GetType().Name}): {ex.Message}");
            return 2;
        }
    }

    private string BuildDocumentText(ActivityEvent item) =>
        DocumentStyle.Equals("title-text", StringComparison.OrdinalIgnoreCase)
            ? $"title: {item.WindowTitle} | text: {item.ExtractedText}"
            : RetrievalBenchmarkCorpus.BuildIndexText(item);

    private string BuildMarkdown(
        IModelGateway model,
        int dimensions,
        int eventCount,
        int queryCount,
        RetrievalBenchmarkReport keyword,
        RetrievalBenchmarkReport hybrid)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 真实 embedding 检索质量报告");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 模型：`{model.EmbeddingModelId}` · 维度 {dimensions}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 语料：{eventCount} 条合成事件 / {queryCount} 个查询 / TopK {TopK}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 时间：{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        builder.AppendLine("- 说明：合成语料不含个人数据；查询一半为精确词、一半为同义词。");
        var displayPrefix = QueryPrefix.Replace("\n", "\\n", StringComparison.Ordinal);
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"- 查询指令前缀：{(string.IsNullOrEmpty(displayPrefix) ? "无" : $"`{displayPrefix}`")}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 文档格式：{DocumentStyle}");
        builder.AppendLine();
        builder.Append(keyword.ToMarkdown());
        builder.AppendLine();
        builder.Append(hybrid.ToMarkdown());
        builder.AppendLine();
        builder.Append(RetrievalBenchmarkReport.CompareMarkdown(keyword, hybrid));
        return builder.ToString();
    }

    private sealed class InMemoryBenchmarkRepository(IReadOnlyList<ActivityEvent> events) : IEventRepository
    {
        public Task AppendBatchAsync(IReadOnlyList<ActivityEvent> items, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(items);
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException("Benchmark repository is read-only.");
        }

        public Task AppendWithMediaBatchAsync(IReadOnlyList<EventWithMediaWrite> items, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(items);
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException("Benchmark repository is read-only.");
        }

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

        public Task<ActivityEvent?> GetByIdAsync(string eventId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(events.FirstOrDefault(item => item.Id == eventId));
        }

        public Task<IReadOnlyList<DateOnly>> ListActiveDatesAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<DateOnly>>(
                events.Select(item => item.Timestamp.LocalDate).Distinct().ToArray());
        }

        public Task<bool> DeleteEventAsync(string eventId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException("Benchmark repository is read-only.");
        }

        public Task<int> PurgeDateAsync(DateOnly localDate, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException("Benchmark repository is read-only.");
        }
    }

    private sealed class InMemoryVectorIndex : IVectorIndex
    {
        private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);
        private VectorModelDescriptor? _descriptor;

        public void Upsert(string eventId, ReadOnlyMemory<float> embedding, VectorModelDescriptor descriptor)
        {
            _vectors[eventId] = embedding.ToArray();
            _descriptor = descriptor;
        }

        public Task<VectorIndexMetadata> GetMetadataAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new VectorIndexMetadata("in-memory-benchmark", false, _descriptor, _vectors.Count));
        }

        public Task UpsertAsync(
            string eventId,
            ReadOnlyMemory<float> embedding,
            VectorModelDescriptor model,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Upsert(eventId, embedding, model);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding,
            int topK,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var query = queryEmbedding.ToArray();
            var hits = _vectors
                .Select(pair => new VectorSearchHit(pair.Key, (float)Cosine(query, pair.Value)))
                .Where(hit => hit.Score > 0)
                .OrderByDescending(hit => hit.Score)
                .ThenBy(hit => hit.EventId, StringComparer.Ordinal)
                .Take(topK)
                .ToArray();
            return Task.FromResult<IReadOnlyList<VectorSearchHit>>(hits);
        }

        public Task RebuildAsync(
            VectorModelDescriptor targetModel,
            Func<CancellationToken, IAsyncEnumerable<(string EventId, ReadOnlyMemory<float> Embedding)>> generator,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(generator);
            ct.ThrowIfCancellationRequested();
            _ = targetModel;
            return Task.CompletedTask;
        }

        private static double Cosine(float[] left, float[] right)
        {
            double dot = 0;
            double leftNorm = 0;
            double rightNorm = 0;
            for (var i = 0; i < left.Length && i < right.Length; i++)
            {
                dot += left[i] * right[i];
                leftNorm += left[i] * left[i];
                rightNorm += right[i] * right[i];
            }

            return leftNorm == 0 || rightNorm == 0 ? 0 : dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
        }
    }
}
