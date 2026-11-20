using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Summarization;

/// <summary>
/// Deterministic synthetic corpus for retrieval quality benchmarks. Contains no personal data, so it can be
/// used by both automated tests and the `--benchmark-retrieval` diagnostic command.
/// </summary>
public static class RetrievalBenchmarkCorpus
{
    public static DateOnly FromDate { get; } = new(2026, 11, 16);

    public static DateOnly ToDate { get; } = new(2026, 11, 22);

    public static IReadOnlyList<string> ConceptNames { get; } =
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

    /// <summary>Synonym queries that deliberately avoid the exact wording used by the synthetic events.</summary>
    public static IReadOnlyList<string> SynonymQueries { get; } =
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

    public static IReadOnlyList<ActivityEvent> CreateEvents()
    {
        var events = new List<ActivityEvent>(ConceptNames.Count * 6);
        for (var concept = 0; concept < ConceptNames.Count; concept++)
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

    public static IReadOnlyList<RetrievalBenchmarkQuery> CreateQueries()
    {
        var queries = new List<RetrievalBenchmarkQuery>(ConceptNames.Count * 2);
        for (var concept = 0; concept < ConceptNames.Count; concept++)
        {
            var expected = Enumerable.Range(1, 6).Select(index => $"e{concept:D2}{index:D2}").ToArray();
            queries.Add(new RetrievalBenchmarkQuery(ConceptNames[concept], expected));
            queries.Add(new RetrievalBenchmarkQuery(SynonymQueries[concept], expected));
        }

        return queries;
    }

    public static string BuildIndexText(ActivityEvent item) =>
        $"{item.WindowTitle} {item.ExtractedText}";
}
