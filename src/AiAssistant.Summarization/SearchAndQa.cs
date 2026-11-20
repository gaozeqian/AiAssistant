using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Summarization;

public sealed class HybridSearchOptions
{
    public int TopK { get; init; } = 8;
    public int CandidateLimit { get; init; } = 60;
    public double RrfConstant { get; init; } = 60;
}

/// <summary>
/// Time-filtered keyword + vector retrieval with reciprocal-rank fusion and unified evidence references.
/// </summary>
public sealed class HybridSearchService(
    IEventRepository events,
    IVectorIndex vectors,
    IModelGateway model,
    HybridSearchOptions options,
    ILogger<HybridSearchService> logger) : IEvidenceSearch
{
    public async Task<SearchQueryResult> SearchAsync(
        string query,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (toDate < fromDate)
        {
            (fromDate, toDate) = (toDate, fromDate);
        }

        var keywordEvents = new List<ActivityEvent>();
        await foreach (var item in events.QueryAsync(
                           new EventQuery(
                               FromLocalDate: fromDate,
                               ToLocalDate: toDate,
                               SearchKeyword: query,
                               Limit: options.CandidateLimit),
                           ct))
        {
            keywordEvents.Add(item);
        }

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var matchedBy = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        for (var rank = 0; rank < keywordEvents.Count; rank++)
        {
            AddScore(scores, matchedBy, keywordEvents[rank].Id, "keyword", rank);
        }

        var vectorEvents = new List<ActivityEvent>();
        try
        {
            var embedding = await model.EmbedAsync(
                new ModelEmbeddingRequest([query], Purpose: "evidence-search"),
                ct);
            if (embedding.Vectors.Count > 0)
            {
                var hits = await vectors.SearchAsync(embedding.Vectors[0], options.CandidateLimit, ct);
                for (var rank = 0; rank < hits.Count; rank++)
                {
                    var item = await events.GetByIdAsync(hits[rank].EventId, ct);
                    if (item is null || item.Timestamp.LocalDate < fromDate || item.Timestamp.LocalDate > toDate)
                    {
                        continue;
                    }

                    vectorEvents.Add(item);
                    AddScore(scores, matchedBy, item.Id, "vector", rank);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("Vector recall skipped with {ExceptionType}; keyword results remain.", ex.GetType().Name);
        }

        var allEvents = keywordEvents.Concat(vectorEvents)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var ordered = scores
            .Where(pair => allEvents.ContainsKey(pair.Key))
            .OrderByDescending(pair => pair.Value)
            .ThenByDescending(pair => allEvents[pair.Key].Timestamp.OccurredAtUtc)
            .Take(options.TopK)
            .ToArray();

        var evidence = new List<EvidenceReference>(ordered.Length);
        for (var i = 0; i < ordered.Length; i++)
        {
            var item = allEvents[ordered[i].Key];
            evidence.Add(new EvidenceReference(
                $"E{i + 1}",
                item.Id,
                item.Timestamp.LocalDate,
                item.Timestamp.OccurredAtUtc,
                item.AppName,
                item.WindowTitle,
                BuildExcerpt(item),
                item.MediaRef is not null,
                string.Join("+", matchedBy[item.Id].OrderBy(value => value, StringComparer.Ordinal)),
                Math.Round(ordered[i].Value, 6)));
        }

        return new SearchQueryResult(
            query,
            fromDate,
            toDate,
            evidence,
            keywordEvents.Count,
            vectorEvents.Count);
    }

    private void AddScore(
        Dictionary<string, double> scores,
        Dictionary<string, HashSet<string>> matchedBy,
        string eventId,
        string source,
        int rank)
    {
        var contribution = 1.0 / (options.RrfConstant + rank + 1);
        scores[eventId] = scores.GetValueOrDefault(eventId) + contribution;
        if (!matchedBy.TryGetValue(eventId, out var sources))
        {
            sources = new HashSet<string>(StringComparer.Ordinal);
            matchedBy[eventId] = sources;
        }

        sources.Add(source);
    }

    private static string BuildExcerpt(ActivityEvent item)
    {
        var text = (item.Summary ?? item.ExtractedText ?? item.WindowTitle)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return text.Length <= 240 ? text : text[..240];
    }
}

/// <summary>Evidence-grounded Q&amp;A. The model must cite [E#]; uncited answers are replaced by extractive fallback.</summary>
public sealed partial class EvidenceQuestionAnswerUseCase(
    IEvidenceSearch search,
    IModelGateway model,
    ILogger<EvidenceQuestionAnswerUseCase> logger) : IEvidenceQuestionAnswerer
{
    public async Task<EvidenceQaAnswer> AnswerAsync(
        string question,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var result = await search.SearchAsync(question, fromDate, toDate, ct);
        if (result.Evidence.Count == 0)
        {
            return new EvidenceQaAnswer(
                question,
                "没有找到可验证的本地活动证据，因此不生成推测性回答。",
                [],
                [],
                "none",
                ModelEndpointTier.LocalOffline,
                Degraded: true,
                DegradedReason: "no-evidence");
        }

        var evidenceBlock = new StringBuilder();
        foreach (var item in result.Evidence)
        {
            evidenceBlock.AppendLine(CultureInfo.InvariantCulture,
                $"[{item.EvidenceId}] {item.LocalDate:yyyy-MM-dd} {item.OccurredAtUtc.ToLocalTime():HH:mm} {item.AppName} | {item.Title} | {item.Excerpt} | media={(item.HasMedia ? "yes" : "no")}");
        }

        try
        {
            var response = await model.CompleteAsync(
                new ModelCompletionRequest(
                    """
                    你是本机活动证据问答器。只能使用提供的证据回答，禁止编造。
                    每个关键事实后必须引用证据编号，如 [E1]；证据不足时明确说明。
                    使用简体中文，简洁回答，不要输出 Markdown 表格。
                    """,
                    $"问题：{question}\n证据：\n{evidenceBlock}",
                    Purpose: "evidence-qa",
                    Temperature: 0),
                ct);

            var cited = CitationRegex().Matches(response.Text)
                .Select(match => int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ? index : 0)
                .Where(index => index > 0)
                .Distinct()
                .OrderBy(index => index)
                .ToArray();
            var validIds = result.Evidence.Select(item => item.EvidenceId).ToHashSet(StringComparer.Ordinal);
            var citedIds = cited
                .Where(index => index <= result.Evidence.Count)
                .Select(index => result.Evidence[index - 1].EvidenceId)
                .Where(validIds.Contains)
                .ToArray();
            if (citedIds.Length == 0)
            {
                return ExtractiveFallback(question, result.Evidence, response.ModelId, response.Tier, "uncited-model-answer");
            }

            return new EvidenceQaAnswer(
                question,
                response.Text.Trim(),
                result.Evidence,
                citedIds,
                response.ModelId,
                response.Tier,
                Degraded: false,
                DegradedReason: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Evidence QA degraded with {ExceptionType}; using extractive answer.", ex.GetType().Name);
            return ExtractiveFallback(question, result.Evidence, "extractive", ModelEndpointTier.LocalOffline, "model-unavailable");
        }
    }

    private static EvidenceQaAnswer ExtractiveFallback(
        string question,
        IReadOnlyList<EvidenceReference> evidence,
        string modelId,
        ModelEndpointTier tier,
        string reason)
    {
        var builder = new StringBuilder("根据本地证据：\n");
        foreach (var item in evidence.Take(5))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"- [{item.EvidenceId}] {item.LocalDate:yyyy-MM-dd} {item.AppName}：{item.Title}");
        }

        return new EvidenceQaAnswer(
            question,
            builder.ToString().TrimEnd(),
            evidence,
            evidence.Take(5).Select(item => item.EvidenceId).ToArray(),
            modelId,
            tier,
            Degraded: true,
            DegradedReason: reason);
    }

    [GeneratedRegex(@"\[E(\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex CitationRegex();
}
