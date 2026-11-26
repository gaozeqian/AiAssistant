using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiAssistant.Application.Ports;

namespace AiAssistant.Application.UseCases;

public sealed record RangeReportExport(
    string MarkdownPath,
    string StatsPath,
    string SvgPath,
    string? PngPath = null,
    string? PdfPath = null);

/// <summary>Single-file formats offered by every report export surface.</summary>
public enum ReportExportFormat
{
    Markdown = 0,
    Json = 1,
    Svg = 2,
    Png = 3,
    Pdf = 4
}

public static class ReportExportFormatExtensions
{
    /// <summary>Maps a user-selected save path to a report format; unknown extensions fall back to Markdown.</summary>
    public static ReportExportFormat FromPath(string path) => FromExtension(Path.GetExtension(path));

    /// <summary>Maps a save-dialog file type extension to a report format.</summary>
    public static ReportExportFormat FromExtension(string extension) =>
        extension.Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "md" or "markdown" => ReportExportFormat.Markdown,
            "json" => ReportExportFormat.Json,
            "svg" => ReportExportFormat.Svg,
            "png" => ReportExportFormat.Png,
            "pdf" => ReportExportFormat.Pdf,
            _ => ReportExportFormat.Markdown
        };

    /// <summary>Canonical file extension for a report format.</summary>
    public static string ToExtension(this ReportExportFormat format) => format switch
    {
        ReportExportFormat.Markdown => "md",
        ReportExportFormat.Json => "json",
        ReportExportFormat.Svg => "svg",
        ReportExportFormat.Png => "png",
        ReportExportFormat.Pdf => "pdf",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
}

/// <summary>Weekly / arbitrary-range aggregation plus Markdown/JSON/SVG/PNG/PDF export.</summary>
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
        var statsPath = Path.Combine(destinationDirectory, stem + ".json");
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

    /// <summary>Writes a single format to the exact destination path chosen by the user.</summary>
    public async Task<string> ExportSingleAsync(
        RangeReport report,
        ReportExportFormat format,
        string destinationPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        switch (format)
        {
            case ReportExportFormat.Markdown:
                await File.WriteAllTextAsync(destinationPath, report.Markdown, ct);
                break;
            case ReportExportFormat.Json:
                await File.WriteAllTextAsync(destinationPath, report.StatsJson, ct);
                break;
            case ReportExportFormat.Svg:
                await File.WriteAllTextAsync(destinationPath, RangeReportSvgRenderer.Render(report), ct);
                break;
            case ReportExportFormat.Png:
                if (rasterizer is null)
                {
                    throw new InvalidOperationException("PNG 导出需要图形渲染器。");
                }

                await File.WriteAllBytesAsync(destinationPath, rasterizer.RenderPng(report), ct);
                break;
            case ReportExportFormat.Pdf:
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

public sealed record AnswerExport(
    string MarkdownPath,
    string JsonPath,
    string? SvgPath = null,
    string? PngPath = null,
    string? PdfPath = null);

/// <summary>Shared entry point for timeline search, panel Q&amp;A and voice Q&amp;A so all three cite the same evidence.</summary>
public sealed class EvidenceSearchUseCase(
    IEvidenceSearch search,
    IEvidenceQuestionAnswerer answerer,
    IClock clock,
    IReportDocumentRenderer? documentRenderer = null)
{
    private static readonly JsonSerializerOptions AnswerJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

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

    /// <summary>Writes every available Q&amp;A format into the directory.</summary>
    public async Task<AnswerExport> ExportAnswerAllAsync(
        EvidenceQaAnswer answer,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var stem = BuildAnswerStem();
        var markdownPath = Path.Combine(destinationDirectory, stem + ".md");
        var jsonPath = Path.Combine(destinationDirectory, stem + ".json");
        await File.WriteAllTextAsync(markdownPath, BuildAnswerMarkdown(answer), ct);
        await File.WriteAllTextAsync(jsonPath, BuildAnswerJson(answer), ct);

        string? svgPath = null;
        string? pngPath = null;
        string? pdfPath = null;
        if (documentRenderer is not null)
        {
            var document = BuildAnswerDocument(answer);
            svgPath = Path.Combine(destinationDirectory, stem + ".svg");
            await File.WriteAllTextAsync(svgPath, documentRenderer.RenderSvg(document), ct);
            pngPath = Path.Combine(destinationDirectory, stem + ".png");
            await File.WriteAllBytesAsync(pngPath, documentRenderer.RenderPng(document), ct);
            pdfPath = Path.Combine(destinationDirectory, stem + ".pdf");
            await File.WriteAllBytesAsync(pdfPath, documentRenderer.RenderPdf(document), ct);
        }

        return new AnswerExport(markdownPath, jsonPath, svgPath, pngPath, pdfPath);
    }

    /// <summary>Writes one Q&amp;A format into the directory using a timestamped filename.</summary>
    public Task<string> ExportAnswerAsync(
        EvidenceQaAnswer answer,
        string destinationDirectory,
        ReportExportFormat format,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var path = Path.Combine(
            destinationDirectory,
            BuildAnswerStem() + "." + format.ToExtension());
        return ExportAnswerSingleAsync(answer, format, path, ct);
    }

    /// <summary>Writes one Q&amp;A format to the exact destination path chosen by the user.</summary>
    public async Task<string> ExportAnswerSingleAsync(
        EvidenceQaAnswer answer,
        ReportExportFormat format,
        string destinationPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        switch (format)
        {
            case ReportExportFormat.Markdown:
                await File.WriteAllTextAsync(destinationPath, BuildAnswerMarkdown(answer), ct);
                break;
            case ReportExportFormat.Json:
                await File.WriteAllTextAsync(destinationPath, BuildAnswerJson(answer), ct);
                break;
            case ReportExportFormat.Svg:
                await File.WriteAllTextAsync(
                    destinationPath,
                    RequireDocumentRenderer().RenderSvg(BuildAnswerDocument(answer)),
                    ct);
                break;
            case ReportExportFormat.Png:
                await File.WriteAllBytesAsync(
                    destinationPath,
                    RequireDocumentRenderer().RenderPng(BuildAnswerDocument(answer)),
                    ct);
                break;
            case ReportExportFormat.Pdf:
                await File.WriteAllBytesAsync(
                    destinationPath,
                    RequireDocumentRenderer().RenderPdf(BuildAnswerDocument(answer)),
                    ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        return destinationPath;
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

    private string BuildAnswerStem() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"AiAssistant-问答-{clock.UtcNow.ToLocalTime():yyyyMMdd-HHmmss}");

    private IReportDocumentRenderer RequireDocumentRenderer() =>
        documentRenderer ?? throw new InvalidOperationException("SVG/PNG/PDF 导出需要文档渲染器。");

    private static string BuildAnswerJson(EvidenceQaAnswer answer)
    {
        var payload = new
        {
            question = answer.Question,
            answer = answer.Answer,
            evidence = answer.Evidence.Select(item => new
            {
                evidenceId = item.EvidenceId,
                eventId = item.EventId,
                localDate = item.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                occurredAtUtc = item.OccurredAtUtc,
                appName = item.AppName,
                title = item.Title,
                excerpt = item.Excerpt,
                hasMedia = item.HasMedia,
                matchedBy = item.MatchedBy,
                score = item.Score
            }),
            citedEvidenceIds = answer.CitedEvidenceIds,
            modelId = answer.ModelId,
            endpointTier = answer.EndpointTier.ToString(),
            degraded = answer.Degraded,
            degradedReason = answer.DegradedReason
        };
        return JsonSerializer.Serialize(payload, AnswerJsonOptions);
    }

    private static ReportDocument BuildAnswerDocument(EvidenceQaAnswer answer) =>
        new(
            "证据问答",
            $"问题：{answer.Question} · 模型 {answer.ModelId} · {(answer.Degraded ? $"降级：{answer.DegradedReason}" : "证据充分")}",
            [
                new ReportParagraphSection("回答", [answer.Answer]),
                new ReportBulletSection(
                    "证据",
                    answer.Evidence.Select(item => string.Create(
                        CultureInfo.InvariantCulture,
                        $"[{item.EvidenceId}] {item.LocalDate:yyyy-MM-dd} {item.OccurredAtUtc.ToLocalTime():HH:mm} {item.AppName} | {item.Title} | {item.Excerpt}"))
                        .ToArray()),
                new ReportFieldSection(
                    "元数据",
                    [
                        new ReportField("引用", string.Join(",", answer.CitedEvidenceIds)),
                        new ReportField("模型", answer.ModelId),
                        new ReportField("端点档位", answer.EndpointTier.ToString()),
                        new ReportField("状态", answer.Degraded ? $"降级：{answer.DegradedReason}" : "证据充分")
                    ])
            ]);

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
