using System.Globalization;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;

namespace AiAssistant.Application.UseCases;

public sealed record ReportRequestTicket(string JobId, bool DeduplicatedExistingJob);

public sealed record DailyReportExport(
    string MarkdownPath,
    string JsonPath,
    string SvgPath,
    string? PngPath = null,
    string? PdfPath = null);

public sealed class ReportUseCase(
    IReportJobQueue jobQueue,
    IReportRepository reportRepository,
    IClock clock,
    IReportRasterizer? rasterizer = null)
{
    public Task<DailyReport?> GetLatestAsync(DateOnly date, CancellationToken ct = default) =>
        reportRepository.GetLatestAsync(date, ct);

    public Task<IReadOnlyList<DailyReport>> ListVersionsAsync(DateOnly date, CancellationToken ct = default) =>
        reportRepository.ListVersionsAsync(date, ct);

    public async Task<ReportRequestTicket> RequestAsync(
        DateOnly date,
        bool forceOverwrite,
        CancellationToken ct = default)
    {
        var result = await jobQueue.EnqueueAsync(
            new ReportJobRequest(date, ReportTriggerSource.Manual, forceOverwrite),
            ct);
        return new ReportRequestTicket(result.JobId, result.DeduplicatedExistingPendingJob);
    }

    /// <summary>Waits for the persisted job to finish so the UI can display the exact new version.</summary>
    public async Task<DailyReport?> RequestAndWaitAsync(
        DateOnly date,
        bool forceOverwrite,
        CancellationToken ct = default)
    {
        var before = await reportRepository.GetLatestAsync(date, ct);
        var ticket = await RequestAsync(date, forceOverwrite, ct);
        var deadline = clock.UtcNow.AddMinutes(5);
        while (clock.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var jobs = await jobQueue.ListRecentJobsAsync(50, ct);
            var job = jobs.FirstOrDefault(item => item.Id == ticket.JobId);
            if (job is { Status: ReportJobStatus.Failed })
            {
                throw new InvalidOperationException(job.LastError ?? "Report generation failed.");
            }

            var latest = await reportRepository.GetLatestAsync(date, ct);
            if (latest is not null &&
                (before is null || latest.Id != before.Id || latest.Version > before.Version))
            {
                return latest;
            }

            if (job is { Status: ReportJobStatus.SkippedAlreadyExists })
            {
                return latest;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        throw new TimeoutException("Report generation did not complete within 5 minutes.");
    }

    /// <summary>Writes every report format into the directory, omitting PNG/PDF when no rasterizer is configured.</summary>
    public async Task<DailyReportExport> ExportAsync(
        DailyReport report,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var stem = string.Create(
            CultureInfo.InvariantCulture,
            $"AiAssistant-日报-{report.LocalDate:yyyyMMdd}-v{report.Version}");
        var markdownPath = Path.Combine(destinationDirectory, stem + ".md");
        var jsonPath = Path.Combine(destinationDirectory, stem + ".json");
        var svgPath = Path.Combine(destinationDirectory, stem + ".svg");
        var range = ToRangeReport(report);
        await File.WriteAllTextAsync(markdownPath, report.Markdown, ct);
        await File.WriteAllTextAsync(jsonPath, report.StatsJson, ct);
        await File.WriteAllTextAsync(svgPath, RangeReportSvgRenderer.Render(range), ct);

        string? pngPath = null;
        string? pdfPath = null;
        if (rasterizer is not null)
        {
            pngPath = Path.Combine(destinationDirectory, stem + ".png");
            await File.WriteAllBytesAsync(pngPath, rasterizer.RenderPng(range), ct);
            pdfPath = Path.Combine(destinationDirectory, stem + ".pdf");
            await File.WriteAllBytesAsync(pdfPath, rasterizer.RenderPdf(range), ct);
        }

        return new DailyReportExport(markdownPath, jsonPath, svgPath, pngPath, pdfPath);
    }

    /// <summary>Writes one report format to the exact destination path chosen by the user.</summary>
    public async Task<string> ExportSingleAsync(
        DailyReport report,
        ReportExportFormat format,
        string destinationPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        RangeReport? range = null;
        if (format is ReportExportFormat.Svg or ReportExportFormat.Png or ReportExportFormat.Pdf)
        {
            range = ToRangeReport(report);
        }

        switch (format)
        {
            case ReportExportFormat.Markdown:
                await File.WriteAllTextAsync(destinationPath, report.Markdown, ct);
                break;
            case ReportExportFormat.Json:
                await File.WriteAllTextAsync(destinationPath, report.StatsJson, ct);
                break;
            case ReportExportFormat.Svg:
                await File.WriteAllTextAsync(destinationPath, RangeReportSvgRenderer.Render(range!), ct);
                break;
            case ReportExportFormat.Png:
                if (rasterizer is null)
                {
                    throw new InvalidOperationException("PNG 导出需要图形渲染器。");
                }

                await File.WriteAllBytesAsync(destinationPath, rasterizer.RenderPng(range!), ct);
                break;
            case ReportExportFormat.Pdf:
                if (rasterizer is null)
                {
                    throw new InvalidOperationException("PDF 导出需要图形渲染器。");
                }

                await File.WriteAllBytesAsync(destinationPath, rasterizer.RenderPdf(range!), ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        return destinationPath;
    }

    public static async Task<string> ExportMarkdownAsync(
        DailyReport report,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var fileName = $"AiAssistant-日报-{report.LocalDate:yyyyMMdd}-v{report.Version}.md";
        var path = Path.Combine(destinationDirectory, fileName);
        await File.WriteAllTextAsync(path, report.Markdown, ct);
        return path;
    }

    /// <summary>Writes the daily report markdown to the exact destination path chosen by the user.</summary>
    public static async Task<string> ExportMarkdownToAsync(
        DailyReport report,
        string destinationPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        await File.WriteAllTextAsync(destinationPath, report.Markdown, ct);
        return destinationPath;
    }

    /// <summary>
    /// Adapts a persisted daily report to the chart/PDF renderers. The Markdown remains the single
    /// source of truth for text, while the stats JSON supplies counts for the chart.
    /// </summary>
    private static RangeReport ToRangeReport(DailyReport report)
    {
        var stat = ParseDailyStat(report);
        return new RangeReport(
            report.LocalDate,
            report.LocalDate,
            report.Markdown,
            report.StatsJson,
            [stat],
            report.ModelId,
            false,
            null);
    }

    private static DailyActivityStat ParseDailyStat(DailyReport report)
    {
        var apps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var eventCount = 0;
        var activeMinutes = 0;
        try
        {
            using var document = JsonDocument.Parse(report.StatsJson);
            var root = document.RootElement;
            eventCount = ReadInt32(root, "EventCount", "eventCount");
            activeMinutes = ReadInt32(root, "ActiveMinutes", "activeMinutes");
            if (TryGetProperty(root, "AppCounts", "appCounts", out var counts) &&
                counts.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in counts.EnumerateObject())
                {
                    if (property.Value.TryGetInt32(out var count))
                    {
                        apps[property.Name] = count;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // A malformed stats sidecar must not block Markdown/JSON export; the chart simply stays empty.
        }

        return new DailyActivityStat(report.LocalDate, eventCount, activeMinutes, apps);
    }

    private static int ReadInt32(JsonElement root, string propertyName, string camelCaseName) =>
        TryGetProperty(root, propertyName, camelCaseName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var result)
            ? result
            : 0;

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        string camelCaseName,
        out JsonElement value) =>
        element.TryGetProperty(propertyName, out value) ||
        element.TryGetProperty(camelCaseName, out value);
}
