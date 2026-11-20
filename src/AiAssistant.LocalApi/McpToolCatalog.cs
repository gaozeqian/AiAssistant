using System.Globalization;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;

namespace AiAssistant.LocalApi;

public sealed record McpToolDefinition(string Name, string Description, JsonElement InputSchema);

public sealed record McpToolResult(string Text, bool IsError)
{
    public static McpToolResult Ok(string text) => new(text, false);

    public static McpToolResult Failure(string text) => new(text, true);
}

/// <summary>
/// The complete read-only tool surface of the local MCP endpoint. No tool mutates state, captures,
/// deletes, exports or triggers models; unknown names are rejected instead of forwarded anywhere.
/// </summary>
public sealed class McpToolCatalog
{
    public const string SearchEvents = "search_events";
    public const string ActivityStats = "activity_stats";
    public const string ListReports = "list_reports";
    public const string GetReport = "get_report";

    private const int MaxRangeDays = 366;
    private const int MaxQueryLimit = 100;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ReadOnlyNames = new(StringComparer.Ordinal)
    {
        SearchEvents,
        ActivityStats,
        ListReports,
        GetReport
    };

    private readonly IEventRepository _events;
    private readonly IReportRepository _reports;
    private readonly IClock _clock;

    public McpToolCatalog(IEventRepository events, IReportRepository reports, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(clock);
        _events = events;
        _reports = reports;
        _clock = clock;
    }

    public static IReadOnlyList<McpToolDefinition> Tools { get; } =
    [
        new(
            SearchEvents,
            "按时间范围与关键词检索本地活动事件，返回结构化证据（不含截图或音频字节）。",
            Schema("""
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string", "description": "关键词；省略表示只按时间过滤" },
                    "fromDate": { "type": "string", "description": "起始本地日期 yyyy-MM-dd；默认近 7 天" },
                    "toDate": { "type": "string", "description": "结束本地日期 yyyy-MM-dd；默认今天" },
                    "limit": { "type": "integer", "minimum": 1, "maximum": 100, "description": "返回条数，默认 20" }
                  },
                  "additionalProperties": false
                }
                """)),
        new(
            ActivityStats,
            "按天统计事件数量与主要应用，用于周报前的快速核对。",
            Schema("""
                {
                  "type": "object",
                  "properties": {
                    "fromDate": { "type": "string", "description": "起始本地日期 yyyy-MM-dd；默认近 7 天" },
                    "toDate": { "type": "string", "description": "结束本地日期 yyyy-MM-dd；默认今天" }
                  },
                  "additionalProperties": false
                }
                """)),
        new(
            ListReports,
            "列出某一天已生成的日报版本元数据（不返回正文）。",
            Schema("""
                {
                  "type": "object",
                  "properties": {
                    "date": { "type": "string", "description": "本地日期 yyyy-MM-dd；默认今天" }
                  },
                  "additionalProperties": false
                }
                """)),
        new(
            GetReport,
            "读取某一天指定版本（默认最新）的日报 Markdown 正文，正文已在本机脱敏。",
            Schema("""
                {
                  "type": "object",
                  "properties": {
                    "date": { "type": "string", "description": "本地日期 yyyy-MM-dd；默认今天" },
                    "version": { "type": "integer", "minimum": 1, "description": "日报版本号；省略表示最新版本" }
                  },
                  "additionalProperties": false
                }
                """))
    ];

    public static bool IsReadOnlyTool(string name) =>
        !string.IsNullOrWhiteSpace(name) && ReadOnlyNames.Contains(name);

    public async Task<McpToolResult> InvokeAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!IsReadOnlyTool(name))
        {
            return McpToolResult.Failure(
                $"Tool '{name}' is not available. This endpoint is read-only and exposes only: {string.Join(", ", ReadOnlyNames.OrderBy(item => item, StringComparer.Ordinal))}.");
        }

        if (arguments.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
        {
            return McpToolResult.Failure("Tool arguments must be a JSON object.");
        }

        return name switch
        {
            SearchEvents => await SearchEventsAsync(arguments, ct),
            ActivityStats => await ActivityStatsAsync(arguments, ct),
            ListReports => await ListReportsAsync(arguments, ct),
            _ => await GetReportAsync(arguments, ct)
        };
    }

    private async Task<McpToolResult> SearchEventsAsync(JsonElement arguments, CancellationToken ct)
    {
        var (from, to) = ResolveRange(arguments);
        var query = OptionalString(arguments, "query");
        var limit = OptionalInt(arguments, "limit", 20, 1, MaxQueryLimit);
        var results = new List<EventEvidence>();
        await foreach (var item in _events.QueryAsync(
                           new EventQuery(
                               FromLocalDate: from,
                               ToLocalDate: to,
                               SearchKeyword: string.IsNullOrWhiteSpace(query) ? null : query,
                               Limit: limit),
                           ct))
        {
            results.Add(new EventEvidence(
                item.Id,
                item.Timestamp.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                item.Timestamp.OccurredAtUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
                item.AppName,
                item.WindowTitle,
                Truncate(item.Summary ?? item.ExtractedText, 400),
                item.MediaRef is not null));
        }

        return McpToolResult.Ok(JsonSerializer.Serialize(
            new SearchEventsPayload(from, to, results.Count, results),
            JsonOptions));
    }

    private async Task<McpToolResult> ActivityStatsAsync(JsonElement arguments, CancellationToken ct)
    {
        var (from, to) = ResolveRange(arguments);
        var perDay = new Dictionary<DateOnly, (int Count, Dictionary<string, int> Apps)>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            perDay[date] = (0, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
        }

        var total = 0;
        await foreach (var item in _events.QueryAsync(
                           new EventQuery(FromLocalDate: from, ToLocalDate: to, Limit: 10_000),
                           ct))
        {
            var date = item.Timestamp.LocalDate;
            if (!perDay.TryGetValue(date, out var bucket))
            {
                continue;
            }

            total++;
            var apps = bucket.Apps;
            apps[item.AppName] = apps.GetValueOrDefault(item.AppName) + 1;
            perDay[date] = (bucket.Count + 1, apps);
        }

        var days = new List<DayStat>(perDay.Count);
        foreach (var pair in perDay.OrderBy(item => item.Key))
        {
            var topApps = pair.Value.Apps
                .OrderByDescending(item => item.Value)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .Take(5)
                .Select(item => new AppStat(item.Key, item.Value))
                .ToArray();
            days.Add(new DayStat(
                pair.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                pair.Value.Count,
                topApps));
        }

        return McpToolResult.Ok(JsonSerializer.Serialize(
            new ActivityStatsPayload(from, to, total, days),
            JsonOptions));
    }

    private async Task<McpToolResult> ListReportsAsync(JsonElement arguments, CancellationToken ct)
    {
        var date = ResolveDate(arguments, "date", _clock.Now().LocalDate);
        var versions = await _reports.ListVersionsAsync(date, ct);
        var items = versions
            .OrderByDescending(item => item.Version)
            .Select(item => new ReportMetadata(
                item.Version,
                item.ModelId,
                item.EndpointTier.ToString(),
                item.TriggerSource.ToString(),
                item.RedactionApplied,
                item.GeneratedAtUtc.ToString("O", CultureInfo.InvariantCulture)))
            .ToArray();
        return McpToolResult.Ok(JsonSerializer.Serialize(
            new ReportListPayload(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), items.Length, items),
            JsonOptions));
    }

    private async Task<McpToolResult> GetReportAsync(JsonElement arguments, CancellationToken ct)
    {
        var date = ResolveDate(arguments, "date", _clock.Now().LocalDate);
        var requestedVersion = OptionalInt(arguments, "version", 0, 0, int.MaxValue);
        var versions = await _reports.ListVersionsAsync(date, ct);
        var report = requestedVersion > 0
            ? versions.FirstOrDefault(item => item.Version == requestedVersion)
            : versions.OrderByDescending(item => item.Version).FirstOrDefault();
        if (report is null)
        {
            return McpToolResult.Failure(
                requestedVersion > 0
                    ? $"No report version {requestedVersion} exists for {date:yyyy-MM-dd}."
                    : $"No report exists for {date:yyyy-MM-dd}.");
        }

        return McpToolResult.Ok(JsonSerializer.Serialize(
            new ReportPayload(
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                report.Version,
                report.ModelId,
                report.EndpointTier.ToString(),
                report.TriggerSource.ToString(),
                report.RedactionApplied,
                report.GeneratedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                report.Markdown),
            JsonOptions));
    }

    private (DateOnly From, DateOnly To) ResolveRange(JsonElement arguments)
    {
        var today = _clock.Now().LocalDate;
        var to = ResolveDate(arguments, "toDate", today);
        var from = ResolveDate(arguments, "fromDate", to.AddDays(-6));
        if (to < from)
        {
            (from, to) = (to, from);
        }

        return to.DayNumber - from.DayNumber > MaxRangeDays ? (to.AddDays(-MaxRangeDays), to) : (from, to);
    }

    private static DateOnly ResolveDate(JsonElement arguments, string property, DateOnly fallback)
    {
        var raw = OptionalString(arguments, property);
        return DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : fallback;
    }

    private static string? OptionalString(JsonElement arguments, string property)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static int OptionalInt(JsonElement arguments, string property, int fallback, int min, int max)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var parsed))
        {
            return fallback;
        }

        return Math.Clamp(parsed, min, max);
    }

    private static string? Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static JsonElement Schema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed record EventEvidence(
        string Id,
        string LocalDate,
        string LocalTime,
        string App,
        string Title,
        string? Summary,
        bool MediaAvailable);

    private sealed record SearchEventsPayload(DateOnly From, DateOnly To, int Count, IReadOnlyList<EventEvidence> Events);

    private sealed record AppStat(string App, int Count);

    private sealed record DayStat(string Date, int EventCount, IReadOnlyList<AppStat> TopApps);

    private sealed record ActivityStatsPayload(DateOnly From, DateOnly To, int TotalEvents, IReadOnlyList<DayStat> Days);

    private sealed record ReportMetadata(
        int Version,
        string ModelId,
        string EndpointTier,
        string TriggerSource,
        bool RedactionApplied,
        string GeneratedAt);

    private sealed record ReportListPayload(string Date, int Count, IReadOnlyList<ReportMetadata> Versions);

    private sealed record ReportPayload(
        string Date,
        int Version,
        string ModelId,
        string EndpointTier,
        string TriggerSource,
        bool RedactionApplied,
        string GeneratedAt,
        string Markdown);
}
