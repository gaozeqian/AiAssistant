using System.Globalization;
using System.Text;
using AiAssistant.Application.Ports;

namespace AiAssistant.Application.UseCases;

public sealed record RangeReportExport(
    string MarkdownPath,
    string StatsPath,
    string SvgPath,
    string? PngPath = null,
    string? PdfPath = null);

public enum RangeExportFormat
{
    Markdown = 0,
    StatsJson = 1,
    Svg = 2,
    Png = 3,
    Pdf = 4
}

/// <summary>Weekly / arbitrary-range aggregation plus Markdown + stats export for the reports panel.</summary>
public sealed class RangeReportUseCase(
    IRangeReportService service,
    IClock clock,
    IReportRasterizer? rasterizer = null)
{
    public DateOnly Today => clock.Now().LocalDate;

    /// <summary>Monday-based week containing <paramref name="date"/>.</summary>
    public static (DateOnly From, DateOnly To) WeekContaining(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7;
        var monday = date.AddDays(-offset);
        return (monday, monday.AddDays(6));
    }

    public Task<RangeReport> GenerateAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct = default) =>
        service.GenerateAsync(fromDate, toDate, ct);

    public Task<RangeReport> GenerateWeekAsync(DateOnly anyDateInWeek, CancellationToken ct = default)
    {
        var (from, to) = WeekContaining(anyDateInWeek);
        return service.GenerateAsync(from, to, ct);
    }

    /// <summary>
    /// Writes the Markdown report, stats sidecar and SVG; PNG/PDF are added when a rasterizer is configured.
    /// </summary>
    public async Task<RangeReportExport> ExportAsync(
        RangeReport report,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var stem = string.Create(
            CultureInfo.InvariantCulture,
            $"AiAssistant-区间报告-{report.FromDate:yyyyMMdd}-{report.ToDate:yyyyMMdd}");
        var markdownPath = Path.Combine(destinationDirectory, stem + ".md");
        var statsPath = Path.Combine(destinationDirectory, stem + ".stats.json");
        var svgPath = Path.Combine(destinationDirectory, stem + ".svg");
        await File.WriteAllTextAsync(markdownPath, report.Markdown, ct);
        await File.WriteAllTextAsync(statsPath, report.StatsJson, ct);
        await File.WriteAllTextAsync(svgPath, RangeReportSvgRenderer.Render(report), ct);

        string? pngPath = null;
        string? pdfPath = null;
        if (rasterizer is not null)
        {
            pngPath = Path.Combine(destinationDirectory, stem + ".png");
            await File.WriteAllBytesAsync(pngPath, rasterizer.RenderPng(report), ct);
            pdfPath = Path.Combine(destinationDirectory, stem + ".pdf");
            await File.WriteAllBytesAsync(pdfPath, rasterizer.RenderPdf(report), ct);
        }

        return new RangeReportExport(markdownPath, statsPath, svgPath, pngPath, pdfPath);
    }

    /// <summary>Whether PNG/PDF export is available (requires a rasterizer).</summary>
    public bool SupportsRasterization => rasterizer is not null;

    /// <summary>Writes a single format to the exact destination path chosen by the user.</summary>
    public async Task<string> ExportSingleAsync(
        RangeReport report,
        RangeExportFormat format,
        string destinationPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        switch (format)
        {
            case RangeExportFormat.Markdown:
                await File.WriteAllTextAsync(destinationPath, report.Markdown, ct);
                break;
            case RangeExportFormat.StatsJson:
                await File.WriteAllTextAsync(destinationPath, report.StatsJson, ct);
                break;
            case RangeExportFormat.Svg:
                await File.WriteAllTextAsync(destinationPath, RangeReportSvgRenderer.Render(report), ct);
                break;
            case RangeExportFormat.Png:
                if (rasterizer is null)
                {
                    throw new InvalidOperationException("PNG 导出需要图形渲染器。");
                }

                await File.WriteAllBytesAsync(destinationPath, rasterizer.RenderPng(report), ct);
                break;
            case RangeExportFormat.Pdf:
                if (rasterizer is null)
                {
                    throw new InvalidOperationException("PDF 导出需要图形渲染器。");
                }

                await File.WriteAllBytesAsync(destinationPath, rasterizer.RenderPdf(report), ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        return destinationPath;
    }
}

/// <summary>Shared entry point for timeline search, panel Q&amp;A and voice Q&amp;A so all three cite the same evidence.</summary>
public sealed class EvidenceSearchUseCase(
    IEvidenceSearch search,
    IEvidenceQuestionAnswerer answerer,
    IClock clock)
{
    public DateOnly Today => clock.Now().LocalDate;

    public (DateOnly From, DateOnly To) DefaultRange(int days = 7)
    {
        var to = Today;
        var span = Math.Max(1, days) - 1;
        return (to.AddDays(-span), to);
    }

    public Task<SearchQueryResult> SearchAsync(
        string query,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken ct = default) =>
        search.SearchAsync(query, fromDate, toDate, ct);

    public Task<EvidenceQaAnswer> AskAsync(
        string question,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken ct = default) =>
        answerer.AnswerAsync(question, fromDate, toDate, ct);

    public Task<EvidenceQaAnswer> AskRecentAsync(string question, int days = 7, CancellationToken ct = default)
    {
        var (from, to) = DefaultRange(days);
        return answerer.AnswerAsync(question, from, to, ct);
    }

    /// <summary>Exports a Q&amp;A answer with its evidence appendix so citations stay auditable outside the app.</summary>
    public async Task<string> ExportAnswerAsync(
        EvidenceQaAnswer answer,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"AiAssistant-问答-{clock.UtcNow.ToLocalTime():yyyyMMdd-HHmmss}.md");
        var path = Path.Combine(destinationDirectory, fileName);
        await File.WriteAllTextAsync(path, BuildAnswerMarkdown(answer), ct);
        return path;
    }

    /// <summary>Writes the Q&amp;A markdown to the exact destination path chosen by the user.</summary>
    public static async Task<string> ExportAnswerToAsync(
        EvidenceQaAnswer answer,
        string destinationPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        await File.WriteAllTextAsync(destinationPath, BuildAnswerMarkdown(answer), ct);
        return destinationPath;
    }

    private static string BuildAnswerMarkdown(EvidenceQaAnswer answer)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"# 证据问答：{answer.Question}");
        builder.AppendLine();
        builder.AppendLine("## 回答");
        builder.AppendLine(answer.Answer);
        builder.AppendLine();
        builder.AppendLine("## 证据");
        foreach (var item in answer.Evidence)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"- [{item.EvidenceId}] {item.LocalDate:yyyy-MM-dd} {item.OccurredAtUtc.ToLocalTime():HH:mm} {item.AppName} | {item.Title} | {item.Excerpt}");
        }

        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"模型 `{answer.ModelId}` · 引用 {string.Join(",", answer.CitedEvidenceIds)} · {(answer.Degraded ? $"降级：{answer.DegradedReason}" : "证据充分")}");
        return builder.ToString();
    }
}
