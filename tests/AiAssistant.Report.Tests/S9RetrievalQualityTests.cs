using System.Globalization;
using System.Text;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Summarization;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiAssistant.Report.Tests;

/// <summary>
/// S8-R1 retrieval quality gate on a synthetic labelled corpus: keyword-only versus RRF hybrid.
/// Uses deterministic concept embeddings so fusion/ranking can be measured without a real model.
/// </summary>
public sealed class S9RetrievalQualityTests
{
    private static readonly DateOnly FromDate = new(2026, 11, 16);
    private static readonly DateOnly ToDate = new(2026, 11, 22);

    private static readonly string[] ConceptNames =
    [
        "数据库迁移",
        "支付回调",
        "登录超时",
        "团队周会",
        "文档写作",
        "界面改版",
        "回归测试",
        "部署脚本"
    ];

    private static readonly string[][] ConceptKeywords =
    [
        ["数据库", "迁移", "存储", "引擎", "版本", "迭代"],
        ["支付", "回调", "结算", "失败", "账单"],
        ["登录", "超时", "认证", "异常", "会话"],
        ["周会", "团队", "评审", "同行", "会议"],
        ["文档", "写作", "提纲", "说明", "草稿"],
        ["界面", "改版", "ui", "布局", "设计"],
        ["测试", "回归", "质量", "检查", "缺陷"],
        ["部署", "脚本", "上线", "流水线", "发布"]
    ];

    private static readonly string[] SynonymQueries =
    [
        "存储引擎版本迭代",
        "结算失败",
        "认证异常",
        "同行评审",
        "写作提纲",
        "UI 布局",
        "质量检查",
        "上线流水线"
    ];

    [Fact]
    public async Task HybridRetrieval_OutperformsKeywordOnly_OnSynonymQueries()
    {
        var corpus = BuildCorpus();
        var repository = new QueryableEventRepository(corpus);
        var vectorIndex = new ConceptVectorIndex();
        foreach (var item in corpus)
        {
            vectorIndex.Upsert(item.Id, ConceptEmbeddingModel.Embed(item.WindowTitle + " " + item.ExtractedText));
        }

        var model = new ConceptEmbeddingModel();
        var keywordSearch = new HybridSearchService(
            repository,
            vectorIndex,
            model,
            new HybridSearchOptions { EnableVectorRecall = false },
            NullLogger<HybridSearchService>.Instance);
        var hybridSearch = new HybridSearchService(
            repository,
            vectorIndex,
            model,
            new HybridSearchOptions { EnableVectorRecall = true },
            NullLogger<HybridSearchService>.Instance);
        var queries = BuildQueries();

        var keyword = await RetrievalBenchmarkEvaluator.EvaluateAsync(
            keywordSearch, queries, FromDate, ToDate, "关键词", topK: 8, CancellationToken.None);
        var hybrid = await RetrievalBenchmarkEvaluator.EvaluateAsync(
            hybridSearch, queries, FromDate, ToDate, "混合（关键词 + 概念向量 RRF）", topK: 8, CancellationToken.None);

        await WriteReportAsync(keyword, hybrid);

        Assert.Equal(0.5, keyword.RecallAtK, precision: 2);
        Assert.Equal(0.5, keyword.HitRate, precision: 2);
        Assert.True(hybrid.RecallAtK >= 0.99, $"hybrid recall {hybrid.RecallAtK:P1}");
        Assert.True(hybrid.HitRate >= 0.99, $"hybrid hit rate {hybrid.HitRate:P1}");
        Assert.True(hybrid.Mrr > keyword.Mrr, $"hybrid MRR {hybrid.Mrr:F3} <= keyword {keyword.Mrr:F3}");
        Assert.True(hybrid.Mrr >= 0.99, $"hybrid MRR {hybrid.Mrr:F3}");
        Assert.All(hybrid.Cases, item => Assert.True(item.ReturnedEventIds.Count <= 8));
    }

    [Fact]
    public async Task KeywordOnlyMode_SkipsEmbeddingCalls()
    {
        var corpus = BuildCorpus();
        var repository = new QueryableEventRepository(corpus);
        var model = new ConceptEmbeddingModel();
        var search = new HybridSearchService(
            repository,
            new ConceptVectorIndex(),
            model,
            new HybridSearchOptions { EnableVectorRecall = false },
            NullLogger<HybridSearchService>.Instance);

        _ = await search.SearchAsync("数据库迁移", FromDate, ToDate, CancellationToken.None);

        Assert.Equal(0, model.EmbedCalls);
    }

    private static List<ActivityEvent> BuildCorpus()
    {
        var events = new List<ActivityEvent>(48);
        for (var concept = 0; concept < ConceptNames.Length; concept++)
        {
            for (var index = 1; index <= 6; index++)
            {
                var date = FromDate.AddDays((concept + index) % 6);
                var timestamp = OccurredTimestamp.FromInstant(
                    new DateTimeOffset(date.ToDateTime(new TimeOnly(9, index)), TimeSpan.Zero),
                    TimeZoneInfo.Utc);
                events.Add(new ActivityEvent(
                    $"e{concept:D2}{index:D2}",
                    timestamp,
                    SourceKind.WindowActivity,
                    index % 2 == 0 ? "Rider" : "Chrome",
                    $"{ConceptNames[concept]} 记录 {index}",
                    extractedText: $"{ConceptNames[concept]} 任务 {index}，用于合成检索评测。"));
            }
        }

        return events;
    }

    private static List<RetrievalBenchmarkQuery> BuildQueries()
    {
        var queries = new List<RetrievalBenchmarkQuery>(ConceptNames.Length * 2);
        for (var concept = 0; concept < ConceptNames.Length; concept++)
        {
            var expected = Enumerable.Range(1, 6).Select(index => $"e{concept:D2}{index:D2}").ToArray();
            queries.Add(new RetrievalBenchmarkQuery(ConceptNames[concept], expected));
            queries.Add(new RetrievalBenchmarkQuery(SynonymQueries[concept], expected));
        }

        return queries;
    }

    private static async Task WriteReportAsync(RetrievalBenchmarkReport keyword, RetrievalBenchmarkReport hybrid)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 检索质量基准报告（合成语料）");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 语料：{48} 条合成事件 / {keyword.Cases.Count} 个查询 / TopK {keyword.TopK}");
        builder.AppendLine("- 说明：概念向量为确定性测试替身，用于验证召回融合与排序；真实 embedding 模型质量仍待真机评测。");
        builder.AppendLine();
        builder.Append(keyword.ToMarkdown());
        builder.AppendLine();
        builder.Append(hybrid.ToMarkdown());
        builder.AppendLine();
        builder.Append(RetrievalBenchmarkReport.CompareMarkdown(keyword, hybrid));

        try
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AiAssistant.sln")))
            {
                directory = directory.Parent;
            }

            if (directory is null)
            {
                return;
            }

            var outputDirectory = Path.Combine(directory.FullName, "artifacts", "benchmarks");
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "retrieval-benchmark.md"),
                builder.ToString());
        }
        catch (IOException)
        {
            // Report writing must never fail the quality gate.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static readonly VectorModelDescriptor DefaultModel = new("concept-embedding", "test-1", 8);

    private sealed class ConceptEmbeddingModel : IModelGateway
    {
        public int EmbedCalls { get; private set; }

        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.ModelGateway, "concept embedding");
        public string ChatModelId => "concept-chat";
        public string EmbeddingModelId => "concept-embedding";

        public static float[] Embed(string? text)
        {
            var vector = new float[ConceptKeywords.Length];
            if (string.IsNullOrWhiteSpace(text))
            {
                return vector;
            }

            var lowered = text.ToLowerInvariant();
            for (var concept = 0; concept < ConceptKeywords.Length; concept++)
            {
                foreach (var keyword in ConceptKeywords[concept])
                {
                    var index = 0;
                    while ((index = lowered.IndexOf(keyword, index, StringComparison.Ordinal)) >= 0)
                    {
                        vector[concept] += 1;
                        index += keyword.Length;
                    }
                }
            }

            return vector;
        }

        public Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct) =>
            Task.FromResult(new ModelCompletionResult("{}", ChatModelId, ModelEndpointTier.LocalOffline, false, TimeSpan.Zero));

        public Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct)
        {
            EmbedCalls++;
            var vectors = request.Inputs.Select(input => new ReadOnlyMemory<float>(Embed(input))).ToArray();
            return Task.FromResult(new ModelEmbeddingResult(vectors, EmbeddingModelId, ConceptKeywords.Length));
        }

        public Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new ModelCapabilitySnapshot(true, true, ChatModelId, EmbeddingModelId, ModelEndpointTier.LocalOffline, "ok"));
    }

    private sealed class ConceptVectorIndex : IVectorIndex
    {
        private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);

        public void Upsert(string eventId, float[] vector) => _vectors[eventId] = vector;

        public Task<VectorIndexMetadata> GetMetadataAsync(CancellationToken ct) =>
            Task.FromResult(new VectorIndexMetadata("concept", false, DefaultModel, _vectors.Count));

        public Task UpsertAsync(string eventId, ReadOnlyMemory<float> embedding, VectorModelDescriptor model, CancellationToken ct)
        {
            _vectors[eventId] = embedding.ToArray();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding,
            int topK,
            CancellationToken ct)
        {
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
            CancellationToken ct) =>
            Task.CompletedTask;

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
