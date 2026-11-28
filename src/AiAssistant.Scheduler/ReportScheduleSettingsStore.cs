using System.Globalization;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Configuration;

namespace AiAssistant.Scheduler;

/// <summary>
/// Persists the daily-report schedule as a small JSON document in the user data directory.
/// The UI is the primary editor; the environment variables remain a fallback for headless/CLI
/// launches that have never saved settings through the UI.
/// </summary>
public sealed class ReportScheduleSettingsStore : IReportScheduleSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string[] TimeFormats = ["HH:mm", "H:mm", "HH:mm:ss"];

    private readonly string _filePath;
    private readonly Lock _sync = new();
    private ReportScheduleSettings _settings;

    public ReportScheduleSettingsStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "report-schedule.json");
        _settings = LoadInitial();
    }

    public ReportScheduleSettings Load()
    {
        lock (_sync)
        {
            return _settings;
        }
    }

    public async Task SaveAsync(ReportScheduleSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var document = new ReportScheduleDocument(
            settings.DailyLocalTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            settings.Enabled,
            settings.WeeklyEnabled,
            settings.WeeklyDay.ToString(),
            settings.WeeklyLocalTime.ToString("HH:mm", CultureInfo.InvariantCulture));
        var tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, ct);
            }

            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        lock (_sync)
        {
            _settings = settings;
        }
    }

    private ReportScheduleSettings LoadInitial()
    {
        if (TryLoadPersisted(out var persisted))
        {
            return persisted;
        }

        return LoadEnvironmentFallback();
    }

    private bool TryLoadPersisted(out ReportScheduleSettings settings)
    {
        settings = ReportScheduleSettings.Default;
        if (!File.Exists(_filePath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = JsonSerializer.Deserialize<ReportScheduleDocument>(stream, JsonOptions);
            if (document is null ||
                !TimeOnly.TryParseExact(
                    document.Time,
                    TimeFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var time))
            {
                return false;
            }

            // 旧版本文件没有周报字段：缺失时按"关闭 + 周日 18:00"补齐，不覆盖已有值。
            var weeklyTime = TimeOnly.TryParseExact(
                document.WeeklyTime ?? DefaultWeeklyTime,
                TimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedWeekly)
                ? parsedWeekly
                : TimeOnly.ParseExact(DefaultWeeklyTime, "HH:mm", CultureInfo.InvariantCulture);
            var weeklyDay = Enum.TryParse<DayOfWeek>(document.WeeklyDay, ignoreCase: true, out var parsedDay)
                ? parsedDay
                : DayOfWeek.Sunday;

            settings = new ReportScheduleSettings(
                time,
                document.Enabled,
                document.WeeklyEnabled,
                weeklyDay,
                weeklyTime);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ReportScheduleSettings LoadEnvironmentFallback()
    {
        var rawTime = Environment.GetEnvironmentVariable(EnvironmentNames.ReportTime);
        var time = TimeOnly.TryParse(
            rawTime,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : ReportScheduleSettings.Default.DailyLocalTime;
        var disabled = string.Equals(
            Environment.GetEnvironmentVariable(EnvironmentNames.ReportDisableSchedule),
            "true",
            StringComparison.OrdinalIgnoreCase);
        return new ReportScheduleSettings(
            time,
            !disabled,
            ReportScheduleSettings.Default.WeeklyEnabled,
            ReportScheduleSettings.Default.WeeklyDay,
            ReportScheduleSettings.Default.WeeklyLocalTime);
    }

    private const string DefaultWeeklyTime = "18:00";

    private sealed record ReportScheduleDocument(
        string Time,
        bool Enabled,
        bool WeeklyEnabled = false,
        string? WeeklyDay = null,
        string? WeeklyTime = null);
}
