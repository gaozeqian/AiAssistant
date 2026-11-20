using System.Globalization;
using System.Text;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Summarization;

public sealed class ReportGenerationOptions
{
    public int MaxStructuredRetries { get; init; } = 2;
    public int MaxSegments { get; init; } = 24;
    public int MaxEventsPerSegment { get; init; } = 80;
    public TimeSpan InactivityGap { get; init; } = TimeSpan.FromMinutes(12);
    public ModelEndpointTier DefaultTier { get; init; } = ModelEndpointTier.LocalOffline;
}

/// <summary>
/// Deterministic event segmentation, map summaries, structured reduce, schema validation and versioned persistence.
/// Model failures produce a failed job, never a fabricated "successful" empty report.
/// </summary>
public sealed class ReportGenerationRunner : IReportGenerationRunner
{
    private static readonly JsonSerializerOptions StatsJsonOptions = new() { WriteIndented = false };
    private readonly IEventRepository _events;
    private readonly IModelGateway _model;
    private readonly IReportRepository _reports;
    private readonly IClock _clock;
    private readonly ITextRedactor _redactor;
    private readonly ReportGenerationOptions _options;
    private readonly ILogger<ReportGenerationRunner> _logger;
    private readonly ActivitySegmenter _segmenter;

    public ReportGenerationRunner(
        IEventRepository events,
        IModelGateway model,
        IReportRepository reports,
        IClock clock,
        ITextRedactor redactor,
        ReportGenerationOptions options,
        ILogger<ReportGenerationRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _events = events;
        _model = model;
        _reports = reports;
        _clock = clock;
        _redactor = redactor;
        _options = options;
        _logger = logger;
        _segmenter = new ActivitySegmenter(options.InactivityGap, options.MaxEventsPerSegment);
    }

    public async Task<ReportGenerationOutcome> GenerateAsync(ReportJobRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var existing = await _reports.GetLatestAsync(request.TargetDate, ct);
        if (existing is not null && !request.ForceOverwrite)
        {
            return new ReportGenerationOutcome(existing, SkippedExisting: true, FailureReason: null);
        }

        try
        {
            var events = new List<ActivityEvent>();
            await foreach (var item in _events.QueryAsync(
                               new EventQuery(LocalDate: request.TargetDate, Limit: 10_000),
                               ct))
            {
                events.Add(item);
            }

            if (events.Count == 0)
            {
                return new ReportGenerationOutcome(null, false, "该日期没有可生成日报的活动记录。");
            }

            var segments = _segmenter.Segment(events).Take(_options.MaxSegments).ToArray();
            var mapped = new List<string>(segments.Length);
            var mapRedacted = false;
            foreach (var segment in segments)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var response = await _model.CompleteAsync(
                        new ModelCompletionRequest(
                            BuildMapSystemPrompt(),
                            BuildMapUserPrompt(segment),
                            Tier: _options.DefaultTier,
                            Purpose: "report-map",
                            Temperature: 0.2,
                            MaxOutputTokens: 600),
                        ct);
                    if (!string.IsNullOrWhiteSpace(response.Text))
                    {
                        mapped.Add(response.Text.Trim());
                        mapRedacted |= response.RedactionApplied;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Report map stage failed for segment {SegmentIndex} with {ExceptionType}.",
                        segment.Index,
                        ex.GetType().Name);
                }
            }

            if (mapped.Count == 0)
            {
                return new ReportGenerationOutcome(null, false, "模型未能生成任何分段摘要。");
            }

            var stats = BuildStats(request.TargetDate, events, segments);
            var (document, reduceResult) = await ReduceWithRetryAsync(request.TargetDate, mapped, stats, ct);
            if (document is null || reduceResult is null)
            {
                return new ReportGenerationOutcome(null, false, "模型输出未通过结构化日报 Schema 校验。");
            }

            stats = stats with
            {
                Standup = document.Standup,
                ModelId = reduceResult.ModelId,
                EndpointTier = reduceResult.Tier.ToString(),
                RedactionApplied = reduceResult.RedactionApplied || mapRedacted
            };

            var markdown = ReportMarkdownBuilder.Build(request.TargetDate, document, stats);
            var statsJson = JsonSerializer.Serialize(stats, StatsJsonOptions);
            var redactedMarkdown = _redactor.Redact(markdown);
            var redactedStats = _redactor.Redact(statsJson);
            var redactionApplied = stats.RedactionApplied ||
                                   redactedMarkdown.Count + redactedStats.Count > 0;

            var report = await _reports.AppendAsync(
                request.TargetDate,
                redactedMarkdown.Text,
                redactedStats.Text,
                reduceResult.ModelId,
                reduceResult.Tier,
                redactionApplied,
                request.TriggerSource,
                _clock.UtcNow,
                ct);
            return new ReportGenerationOutcome(report, SkippedExisting: false, FailureReason: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Daily report generation failed with {ExceptionType}.", ex.GetType().Name);
            return new ReportGenerationOutcome(null, false, $"日报生成失败（{ex.GetType().Name}）。");
        }
    }

    private async Task<(DailyReportDocument? Document, ModelCompletionResult? Result)> ReduceWithRetryAsync(
        DateOnly date,
        IReadOnlyList<string> mappedSummaries,
        DailyReportStats stats,
        CancellationToken ct)
    {
        string? lastSchemaError = null;
        for (var attempt = 0; attempt <= _options.MaxStructuredRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var userPrompt = BuildReduceUserPrompt(date, mappedSummaries, stats, lastSchemaError);
            ModelCompletionResult result;
            try
            {
                result = await _model.CompleteAsync(
                    new ModelCompletionRequest(
                        BuildReduceSystemPrompt(),
                        userPrompt,
                        Tier: _options.DefaultTier,
                        Purpose: "report-reduce",
                        Temperature: attempt == 0 ? 0.2 : 0.0,
                        MaxOutputTokens: 2400),
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Report reduce stage failed with {ExceptionType}.", ex.GetType().Name);
                continue;
            }

            if (ReportJsonSchema.TryParse(result.Text, out var document, out var error))
            {
                return (document, result);
            }

            lastSchemaError = error;
            _logger.LogInformation("Report JSON rejected on attempt {Attempt}: {SchemaError}.", attempt + 1, error);
        }

        return (null, null);
    }

    private static DailyReportStats BuildStats(
        DateOnly date,
        List<ActivityEvent> events,
        ActivitySegment[] segments)
    {
        var apps = events
            .GroupBy(item => item.AppName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var activeMinutes = (int)Math.Min(1440, segments.Sum(segment => Math.Max(1, segment.Duration.TotalMinutes)));
        return new DailyReportStats(
            date,
            events.Count,
            segments.Length,
            activeMinutes,
            apps,
            new ReportStandup(),
            "pending",
            ModelEndpointTier.LocalOffline.ToString(),
            false);
    }

    private static string BuildMapSystemPrompt() => """
        你是本机个人活动追踪助手的摘要器。请用简体中文概括给定工作片段的实际内容，
        只输出 2-4 句纯文本摘要，不要 JSON、Markdown 标题或猜测不存在的事实。
        不得输出密码、密钥、完整文件路径或个人信息。
        """;

    private static string BuildMapUserPrompt(ActivitySegment segment)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"片段 {segment.Index + 1}：{segment.StartUtc.ToLocalTime():HH:mm}-{segment.EndUtc.ToLocalTime():HH:mm}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"应用：{string.Join(", ", segment.AppCounts.Keys.Take(8))}");
        foreach (var item in segment.Events.Take(40))
        {
            var text = (item.Summary ?? item.ExtractedText ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            if (text.Length > 200)
            {
                text = text[..200];
            }

            builder.AppendLine(CultureInfo.InvariantCulture,
                $"- {item.Timestamp.OccurredAtUtc.ToLocalTime():HH:mm} [{item.AppName}] {item.WindowTitle}: {text}");
        }

        return builder.ToString();
    }

    private static string BuildReduceSystemPrompt() => $$"""
        你是本机个人活动追踪助手的日报生成器。请根据分段摘要生成简体中文日报。
        只输出一个合法 JSON 对象，不要 Markdown 代码围栏或额外解释。结构必须严格为：
        {{ReportJsonSchema.SchemaDescription}}
        事实必须来自输入；无法确定的计划或阻塞项使用空字符串/空数组。禁止输出密码、密钥、完整路径或个人信息。
        """;

    private static string BuildReduceUserPrompt(
        DateOnly date,
        IReadOnlyList<string> mappedSummaries,
        DailyReportStats stats,
        string? lastSchemaError)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"日期：{date:yyyy-MM-dd}");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"事件数：{stats.EventCount}，片段数：{stats.SegmentCount}，活跃分钟：{stats.ActiveMinutes}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"应用分布：{string.Join(", ", stats.AppCounts.Select(kv => $"{kv.Key}={kv.Value}"))}");
        builder.AppendLine("分段摘要：");
        var total = 0;
        foreach (var summary in mappedSummaries)
        {
            if (total + summary.Length > 12_000)
            {
                break;
            }

            builder.AppendLine(CultureInfo.InvariantCulture, $"- {summary}");
            total += summary.Length;
        }

        if (!string.IsNullOrEmpty(lastSchemaError))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"上一次输出未通过校验（{lastSchemaError}）。必须重新输出完整合法 JSON，包含 overview/highlights/projects/timeline/standup。");
        }

        return builder.ToString();
    }
}

public sealed record DailyReportStats(
    DateOnly LocalDate,
    int EventCount,
    int SegmentCount,
    int ActiveMinutes,
    IReadOnlyDictionary<string, int> AppCounts,
    ReportStandup Standup,
    string ModelId,
    string EndpointTier,
    bool RedactionApplied);

public static class ReportMarkdownBuilder
{
    public static string Build(DateOnly date, DailyReportDocument document, DailyReportStats stats)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"# {date:yyyy年MM月dd日} 活动日报");
        builder.AppendLine();
        builder.AppendLine("## 概览");
        builder.AppendLine(document.Overview.Trim());
        builder.AppendLine();
        builder.AppendLine("## 关键成果");
        foreach (var highlight in document.Highlights)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {highlight.Trim()}");
        }

        if (document.Projects.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 项目进展");
            builder.AppendLine("| 项目 | 进展 | 时长 |");
            builder.AppendLine("|---|---|---|");
            foreach (var project in document.Projects)
            {
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"| {EscapeCell(project.Name)} | {EscapeCell(project.Summary)} | {project.DurationMinutes} 分钟 |");
            }
        }

        if (document.Timeline.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 时间线");
            foreach (var item in document.Timeline)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- `{item.Time}` {item.Activity.Trim()}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 站会视图");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- **昨天完成**：{document.Standup.Yesterday}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- **今天计划**：{document.Standup.Today}");
        builder.AppendLine(document.Standup.Blockers.Count == 0
            ? "- **阻塞项**：无"
            : $"- **阻塞项**：{string.Join("；", document.Standup.Blockers)}");
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"统计：{stats.EventCount} 条事件 · {stats.SegmentCount} 个片段 · {stats.ActiveMinutes} 分钟活跃 · 模型 `{stats.ModelId}` · 档位 {stats.EndpointTier} · 脱敏 {(stats.RedactionApplied ? "已应用" : "未触发")}");
        return builder.ToString();
    }

    private static string EscapeCell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Trim();
}
