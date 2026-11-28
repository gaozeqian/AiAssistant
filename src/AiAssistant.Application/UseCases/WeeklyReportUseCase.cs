using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;

namespace AiAssistant.Application.UseCases;

/// <summary>
/// Generates and persists weekly range reports. Daily reports go through <see cref="ReportUseCase"/>
/// and its job queue; weekly ones are generated on demand and stored versioned so the reports tab can
/// list, refresh and export them exactly like daily versions.
/// </summary>
public sealed class WeeklyReportUseCase(
    RangeReportUseCase range,
    IWeeklyReportRepository repository,
    IClock clock)
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Generates the week containing <paramref name="anyDateInWeek"/>. Unless
    /// <paramref name="forceOverwrite"/> is set, an existing version is returned unchanged.
    /// </summary>
    public async Task<WeeklyReport> GenerateAsync(
        DateOnly anyDateInWeek,
        bool forceOverwrite,
        ReportTriggerSource triggerSource,
        CancellationToken ct = default)
    {
        var (from, to) = RangeReportUseCase.WeekContaining(anyDateInWeek);
        if (!forceOverwrite && await repository.GetLatestAsync(from, ct) is { } existing)
        {
            return existing;
        }

        var report = await range.GenerateWeekAsync(anyDateInWeek, ct);
        return await repository.AppendAsync(
            from,
            to,
            report.Markdown,
            JsonSerializer.Serialize(report, PayloadOptions),
            report.ModelId,
            triggerSource,
            clock.UtcNow,
            ct);
    }

    public Task<IReadOnlyList<WeeklyReport>> ListVersionsAsync(
        DateOnly anyDateInWeek,
        CancellationToken ct = default) =>
        repository.ListVersionsAsync(RangeReportUseCase.WeekContaining(anyDateInWeek).From, ct);

    /// <summary>Rebuilds the exportable payload of a stored version; null when the payload is unreadable.</summary>
    public static RangeReport? ToRangeReport(WeeklyReport stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        try
        {
            return JsonSerializer.Deserialize<RangeReport>(stored.PayloadJson, PayloadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
