using System.Globalization;
using System.Text;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Summarization;

/// <summary>Weekly/arbitrary-range report with deterministic stats, optional local model overview and Markdown export.</summary>
public sealed class ActivityRangeReportService(
    IEventRepository events,
    IModelGateway model,
    ITextRedactor redactor,
    ILogger<ActivityRangeReportService> logger) : IRangeReportService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public async Task<RangeReport> GenerateAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken ct)
    {
        if (toDate < fromDate)
        {
            (fromDate, toDate) = (toDate, fromDate);
        }

        var all = new List<ActivityEvent>();
        await foreach (var item in events.QueryAsync(
                           new EventQuery(FromLocalDate: fromDate, ToLocalDate: toDate, Limit: 10_000),
                           ct))
        {
            all.Add(item);
        }

        var daily = BuildDailyStats(all, fromDate, toDate);
        if (all.Count == 0)
        {
            const string empty = "# 区间活动报告\n\n该区间没有可用的本地活动记录。";
            return new RangeReport(fromDate, toDate, empty, "{}", daily, "none", true, "no-evidence");
        }

        var overview = BuildDeterministicOverview(daily, all);
        var modelId = "extractive";
        var degraded = false;
        string? degradedReason = null;
        try
        {
            if (model.Capability.IsOperational)
            {
                var prompt = BuildModelPrompt(fromDate, toDate, daily, all);
                var response = await model.CompleteAsync(
                    new ModelCompletionRequest(
                        "你是本机活动周报助手。只根据给定统计与记录写 2-3 段简体中文总结，不编造，不输出 Markdown 表格。",
                        prompt,
                        Purpose: "range-report",
                        Temperature: 0.2,
                        MaxOutputTokens: 900),
                    ct);
                if (!string.IsNullOrWhiteSpace(response.Text))
                {
                    overview = response.Text.Trim();
                    modelId = response.ModelId;
                }
                else
                {
                    degraded = true;
                    degradedReason = "empty-model-response";
                }
            }
            else
            {
                degraded = true;
                degradedReason = "model-unavailable";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Range report model summary degraded with {ExceptionType}.", ex.GetType().Name);
            degraded = true;
            degradedReason = "model-unavailable";
        }

        var markdown = BuildMarkdown(fromDate, toDate, overview, daily, all, modelId, degraded);
        var redacted = redactor.Redact(markdown);
        var statsJson = JsonSerializer.Serialize(
            new
            {
                FromDate = fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ToDate = toDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                EventCount = all.Count,
                DailyStats = daily,
                ModelId = modelId,
                Degraded = degraded
            },
            JsonOptions);
        return new RangeReport(
            fromDate,
            toDate,
            redacted.Text,
            redactor.Redact(statsJson).Text,
            daily,
            modelId,
            degraded,
            degradedReason);
    }

    private static List<DailyActivityStat> BuildDailyStats(
        List<ActivityEvent> events,
        DateOnly fromDate,
        DateOnly toDate)
    {
        var result = new List<DailyActivityStat>();
        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            var dayEvents = events.Where(item => item.Timestamp.LocalDate == date).ToArray();
            var segments = new ActivitySegmenter().Segment(dayEvents);
            var activeMinutes = (int)Math.Min(1440, segments.Sum(segment => Math.Max(1, segment.Duration.TotalMinutes)));
            var apps = dayEvents
                .GroupBy(item => item.AppName, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .Take(6)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            result.Add(new DailyActivityStat(date, dayEvents.Length, activeMinutes, apps));
        }

        return result;
    }

    private static string BuildDeterministicOverview(
        List<DailyActivityStat> daily,
        List<ActivityEvent> events)
    {
        var totalMinutes = daily.Sum(item => item.ActiveMinutes);
        var activeDays = daily.Count(item => item.EventCount > 0);
        var topApps = events
            .GroupBy(item => item.AppName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Take(4)
            .Select(group => $"{group.Key}（{group.Count()} 条）");
        return $"该区间共记录 {events.Count} 条活动，活跃 {activeDays} 天，累计活跃约 {totalMinutes} 分钟。" +
               $"主要应用为 {string.Join("、", topApps)}。";
    }

    private static string BuildModelPrompt(
        DateOnly fromDate,
        DateOnly toDate,
        List<DailyActivityStat> daily,
        List<ActivityEvent> events)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"区间：{fromDate:yyyy-MM-dd} 至 {toDate:yyyy-MM-dd}");
        builder.AppendLine("每日统计：");
        foreach (var item in daily)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"- {item.Date:yyyy-MM-dd}: {item.EventCount} 条, {item.ActiveMinutes} 分钟, 应用 {string.Join(",", item.AppCounts.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }

        builder.AppendLine("代表性记录：");
        foreach (var item in events
                     .OrderByDescending(item => item.Timestamp.OccurredAtUtc)
                     .Take(30))
        {
            var text = (item.Summary ?? item.ExtractedText ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            if (text.Length > 160)
            {
                text = text[..160];
            }

            builder.AppendLine(CultureInfo.InvariantCulture,
                $"- {item.Timestamp.LocalDate:yyyy-MM-dd} {item.AppName} | {item.WindowTitle} | {text}");
        }

        return builder.ToString();
    }

    private static string BuildMarkdown(
        DateOnly fromDate,
        DateOnly toDate,
        string overview,
        List<DailyActivityStat> daily,
        List<ActivityEvent> events,
        string modelId,
        bool degraded)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"# {fromDate:yyyy年MM月dd日} – {toDate:yyyy年MM月dd日} 活动报告");
        builder.AppendLine();
        builder.AppendLine("## 总结");
        builder.AppendLine(overview);
        builder.AppendLine();
        builder.AppendLine("## 每日统计");
        builder.AppendLine("| 日期 | 事件数 | 活跃分钟 | 主要应用 |");
        builder.AppendLine("|---|---|---|---|");
        foreach (var item in daily)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {item.Date:yyyy-MM-dd} | {item.EventCount} | {item.ActiveMinutes} | {string.Join("、", item.AppCounts.Keys.Take(4))} |");
        }

        builder.AppendLine();
        builder.AppendLine("## 应用分布");
        foreach (var app in events
                     .GroupBy(item => item.AppName, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(group => group.Count())
                     .Take(10))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {app.Key}: {app.Count()} 条");
        }

        builder.AppendLine();
        builder.AppendLine("## 代表性记录");
        foreach (var item in events
                     .OrderByDescending(item => item.Timestamp.OccurredAtUtc)
                     .Take(15))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"- {item.Timestamp.LocalDate:yyyy-MM-dd} {item.AppName}：{item.WindowTitle}");
        }

        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"模型 `{modelId}` · {(degraded ? "统计模式（模型不可用）" : "本地模型摘要")} · 已应用本地脱敏");
        return builder.ToString();
    }
}
