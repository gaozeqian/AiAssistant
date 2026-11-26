using System.Text;
using AiAssistant.Application.Ports;
using AiAssistant.Export;
using SkiaSharp;

namespace AiAssistant.Report.Tests;

/// <summary>Q&amp;A, diagnostics and benchmark exports share one structured document renderer.</summary>
[Trait("Category", "Unit")]
public sealed class SkiaReportDocumentRendererTests
{
    [Fact]
    public void RenderSvgPngPdf_FromStructuredDocument()
    {
        var document = CreateDocument();
        var renderer = new SkiaReportDocumentRenderer();

        var svg = renderer.RenderSvg(document);
        Assert.Contains("<svg", svg, StringComparison.Ordinal);
        Assert.Contains("本地诊断报告", svg, StringComparison.Ordinal);
        Assert.Contains("平台能力", svg, StringComparison.Ordinal);
        Assert.Contains("第 1 / 1 页", svg, StringComparison.Ordinal);

        var png = renderer.RenderPng(document);
        Assert.True(png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        using var bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);
        Assert.Equal(1920, bitmap.Width);
        Assert.True(bitmap.Height >= 2480, $"height {bitmap.Height}");

        var pdf = renderer.RenderPdf(document);
        Assert.True(pdf.AsSpan(0, 4).SequenceEqual("%PDF"u8));
        Assert.Contains("%%EOF", Encoding.ASCII.GetString(pdf), StringComparison.Ordinal);
        Assert.True(pdf.Length < 2_000_000, $"pdf unexpectedly large: {pdf.Length}");
    }

    [Fact]
    public void RenderSvg_PaginatesLongDocuments()
    {
        var document = CreateDocument() with
        {
            Sections =
            [
                new ReportBulletSection(
                    "日志",
                    Enumerable.Range(0, 160)
                        .Select(index => $"第 {index} 条脱敏日志，包含足够长的说明以触发换行与分页。")
                        .ToArray())
            ]
        };

        var svg = new SkiaReportDocumentRenderer().RenderSvg(document);

        Assert.Contains("第 2 /", svg, StringComparison.Ordinal);
        Assert.Contains("第 3 /", svg, StringComparison.Ordinal);
    }

    private static ReportDocument CreateDocument() =>
        new(
            "本地诊断报告",
            "平台 Windows · 导出时间 2026-11-26 · 仅含脱敏元数据",
            [
                new ReportFieldSection(
                    "数据库",
                    [
                        new ReportField("健康", "是"),
                        new ReportField("事件数", "128")
                    ]),
                new ReportParagraphSection("说明", ["该文档由结构化数据生成，五种导出格式共享同一份内容。"]),
                new ReportBulletSection("平台能力", ["系统托盘：可用", "屏幕录制：需授权"])
            ]);
}
