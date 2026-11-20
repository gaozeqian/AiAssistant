using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiAssistant.Summarization;

public sealed class DailyReportDocument
{
    [JsonPropertyName("overview")]
    public string Overview { get; init; } = string.Empty;

    [JsonPropertyName("highlights")]
    public IReadOnlyList<string> Highlights { get; init; } = [];

    [JsonPropertyName("projects")]
    public IReadOnlyList<ReportProject> Projects { get; init; } = [];

    [JsonPropertyName("timeline")]
    public IReadOnlyList<ReportTimelineItem> Timeline { get; init; } = [];

    [JsonPropertyName("standup")]
    public ReportStandup Standup { get; init; } = new();
}

public sealed class ReportProject
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("durationMinutes")]
    public int DurationMinutes { get; init; }
}

public sealed class ReportTimelineItem
{
    [JsonPropertyName("time")]
    public string Time { get; init; } = string.Empty;

    [JsonPropertyName("activity")]
    public string Activity { get; init; } = string.Empty;
}

public sealed class ReportStandup
{
    [JsonPropertyName("yesterday")]
    public string Yesterday { get; init; } = string.Empty;

    [JsonPropertyName("today")]
    public string Today { get; init; } = string.Empty;

    [JsonPropertyName("blockers")]
    public IReadOnlyList<string> Blockers { get; init; } = [];
}

public static class ReportJsonSchema
{
    public const string SchemaDescription = """
        {
          "overview": "一段 80-200 字的中文总结",
          "highlights": ["3-6 条关键成果"],
          "projects": [{"name": "项目或主题", "summary": "进展", "durationMinutes": 60}],
          "timeline": [{"time": "HH:mm", "activity": "该时段的主要活动"}],
          "standup": {"yesterday": "昨天完成", "today": "今天计划", "blockers": ["阻塞项"]}
        }
        """;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool TryParse(string raw, out DailyReportDocument? document, out string error)
    {
        document = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "empty";
            return false;
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            error = "no-json-object";
            return false;
        }

        try
        {
            document = JsonSerializer.Deserialize<DailyReportDocument>(raw[start..(end + 1)], Options);
        }
        catch (JsonException)
        {
            error = "invalid-json";
            return false;
        }

        if (document is null ||
            string.IsNullOrWhiteSpace(document.Overview) ||
            document.Highlights.Count == 0 ||
            document.Timeline.Count == 0 ||
            document.Standup is null ||
            string.IsNullOrWhiteSpace(document.Standup.Today) ||
            document.Projects.Any(project => string.IsNullOrWhiteSpace(project.Name) || project.DurationMinutes < 0))
        {
            error = "schema-violation";
            document = null;
            return false;
        }

        return true;
    }
}
