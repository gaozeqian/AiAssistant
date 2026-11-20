using System.Diagnostics;
using System.Globalization;
using System.Text;
using AiAssistant.Application.Ports;

namespace AiAssistant.Summarization;

public sealed record RetrievalBenchmarkQuery(string Query, IReadOnlyList<string> ExpectedEventIds);

public sealed record RetrievalBenchmarkCase(
    string Query,
    int Rank,
    double Recall,
    IReadOnlyList<string> ReturnedEventIds,
    long DurationMilliseconds);

public sealed record RetrievalBenchmarkReport(
    string Mode,
    int TopK,
    double RecallAtK,
    double Mrr,
    double HitRate,
    long AverageLatencyMilliseconds,
    IReadOnlyList<RetrievalBenchmarkCase> Cases)
{
    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"## 模式：{Mode}");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 查询数：{Cases.Count} · TopK：{TopK}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Recall@K：{RecallAtK:P1}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 命中率：{HitRate:P1}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- MRR：{Mrr:F3}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- 平均耗时：{AverageLatencyMilliseconds} ms");
        builder.AppendLine();
        builder.AppendLine("| 查询 | 首个命中排名 | 召回 | 耗时 | 返回证据（前 5） |");
        builder.AppendLine("|---|---|---|---|---|");
        foreach (var item in Cases)
        {
            var returned = string.Join(",", item.ReturnedEventIds.Take(5));
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {item.Query} | {(item.Rank == 0 ? "未命中" : item.Rank)} | {item.Recall:P0} | {item.DurationMilliseconds} ms | {returned} |");
        }

        return builder.ToString();
    }

    public static string CompareMarkdown(RetrievalBenchmarkReport baseline, RetrievalBenchmarkReport candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        var builder = new StringBuilder();
        builder.AppendLine("## 对比");
        builder.AppendLine();
        builder.AppendLine("| 指标 | 关键词 | 混合 | 变化 |");
        builder.AppendLine("|---|---|---|---|");
        AppendRow(builder, "Recall@K", baseline.RecallAtK, candidate.RecallAtK);
        AppendRow(builder, "命中率", baseline.HitRate, candidate.HitRate);
        AppendRow(builder, "MRR", baseline.Mrr, candidate.Mrr);
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"| 平均耗时 | {baseline.AverageLatencyMilliseconds} ms | {candidate.AverageLatencyMilliseconds} ms | {candidate.AverageLatencyMilliseconds - baseline.AverageLatencyMilliseconds:+0;-0;0} ms |");
        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, string name, double baseline, double candidate)
    {
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"| {name} | {baseline:P1} | {candidate:P1} | {candidate - baseline:+0.0%;-0.0%;0.0%} |");
    }
}

/// <summary>
/// Measures retrieval quality against a labelled query set. Works with any <see cref="IEvidenceSearch"/>
/// implementation, so the same evaluator can be pointed at keyword-only, hybrid or a real-model deployment.
/// </summary>
public static class RetrievalBenchmarkEvaluator
{
    public static async Task<RetrievalBenchmarkReport> EvaluateAsync(
        IEvidenceSearch search,
        IReadOnlyList<RetrievalBenchmarkQuery> queries,
        DateOnly fromDate,
        DateOnly toDate,
        string mode,
        int topK,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);

        var cases = new List<RetrievalBenchmarkCase>(queries.Count);
        foreach (var query in queries)
        {
            var watch = Stopwatch.StartNew();
            var result = await search.SearchAsync(query.Query, fromDate, toDate, ct);
            watch.Stop();

            var returned = result.Evidence.Select(item => item.EventId).ToArray();
            var expected = query.ExpectedEventIds.Distinct(StringComparer.Ordinal).ToArray();
            var rank = 0;
            for (var index = 0; index < returned.Length; index++)
            {
                if (expected.Contains(returned[index], StringComparer.Ordinal))
                {
                    rank = index + 1;
                    break;
                }
            }

            var matched = expected.Count(id => returned.Contains(id, StringComparer.Ordinal));
            var recall = expected.Length == 0 ? 0 : matched / (double)expected.Length;
            cases.Add(new RetrievalBenchmarkCase(
                query.Query,
                rank,
                recall,
                returned,
                watch.ElapsedMilliseconds));
        }

        var recallAtK = cases.Count == 0 ? 0 : cases.Average(item => item.Recall);
        var hitRate = cases.Count == 0 ? 0 : cases.Count(item => item.Rank > 0) / (double)cases.Count;
        var mrr = cases.Count == 0 ? 0 : cases.Average(item => item.Rank > 0 ? 1.0 / item.Rank : 0);
        var averageLatency = cases.Count == 0 ? 0 : (long)cases.Average(item => item.DurationMilliseconds);
        return new RetrievalBenchmarkReport(mode, topK, recallAtK, hitRate, mrr, averageLatency, cases);
    }
}
