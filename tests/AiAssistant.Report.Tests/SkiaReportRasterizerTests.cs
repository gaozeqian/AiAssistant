using System.Text;
using System.Text.RegularExpressions;
using AiAssistant.Application.Ports;
using AiAssistant.Export;
using SkiaSharp;

namespace AiAssistant.Report.Tests;

/// <summary>PNG/PDF rasterization uses the SkiaSharp build already shipped with Avalonia.</summary>
[Trait("Category", "Unit")]
public sealed class SkiaReportRasterizerTests
{
    [Fact]
    public void RenderPng_IsDecodableAtTwoTimesScale()
    {
        var rasterizer = new SkiaReportRasterizer();

        var png = rasterizer.RenderPng(CreateReport(days: 7));
        Assert.True(png.Length > 1_000, $"png length {png.Length}");
        Assert.True(png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        using var bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);
        Assert.Equal(1920, bitmap.Width);
        Assert.Equal(840, bitmap.Height);
    }

    [Fact]
    public void RenderPdf_ProducesPdfDocument()
    {
        var rasterizer = new SkiaReportRasterizer();

        var pdf = rasterizer.RenderPdf(CreateReport(days: 7));
        Assert.True(pdf.Length > 1_000, $"pdf length {pdf.Length}");
        Assert.True(pdf.Length < 1_000_000, $"pdf unexpectedly large (font embedding?): {pdf.Length}");
        if (PdfSharpReportRenderer.IsSearchableFontAvailable)
        {
            Assert.Contains("/ToUnicode", Encoding.ASCII.GetString(pdf), StringComparison.Ordinal);
        }
        Assert.True(pdf.AsSpan(0, 4).SequenceEqual("%PDF"u8));
        Assert.Contains("%%EOF", Encoding.ASCII.GetString(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void RenderPdf_ExtractsDetailSectionsAndSpreadsThemAcrossPages()
    {
        var longSummary = string.Join(' ', Enumerable.Repeat(
            "该区间共记录大量本地活动，主要应用为 Rider 与浏览器，工作日活跃度高于周末，摘要仅依据本机统计生成。",
            12));
        var report = CreateReport(days: 31) with
        {
            Markdown = $"""
                # 活动报告
                ## 总结
                {longSummary}
                ## 每日统计
                | 日期 | 事件数 | 活跃分钟 | 主要应用 |
                |---|---|---|---|
                | 2026-11-16 | 5 | 30 | Rider |
                ## 应用分布
                - Rider: 21 条
                - 浏览器: 8 条
                ## 代表性记录
                - 2026-11-16 Rider：完成迁移
                - 2026-11-17 浏览器：查阅文档
                ---
                模型 `gemma4:12b` · 已应用本地脱敏
                """
        };

        var sections = RangeReportMarkdownSections.Parse(report.Markdown);
        Assert.Contains("该区间共记录", sections.Summary, StringComparison.Ordinal);
        Assert.Equal(2, sections.Apps.Count);
        Assert.Equal(21, sections.Apps[0].Count);
        Assert.Equal(2, sections.Highlights.Count);

        var pdf = new SkiaReportRasterizer().RenderPdf(report);
        var ascii = Encoding.ASCII.GetString(pdf);
        Assert.True(pdf.AsSpan(0, 4).SequenceEqual("%PDF"u8));
        Assert.True(pdf.Length < 1_000_000, $"pdf unexpectedly large (font embedding?): {pdf.Length}");
        if (PdfSharpReportRenderer.IsSearchableFontAvailable)
        {
            var pageCount = Regex.Count(ascii, @"/Type\s*/Page\b");
            Assert.True(pageCount >= 2, $"expected a multi-page document, got {pageCount} pages");
            Assert.Contains("/ToUnicode", ascii, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DailyMarkdown_ParsesAndRendersDailySections()
    {
        const string markdown = """
            # 2026年11月26日 活动日报
            ## 概览
            完成报告导出格式统一。
            ## 关键成果
            - 日报支持五种格式。
            ## 项目进展
            | 项目 | 进展 | 时长 |
            |---|---|---|
            | AiAssistant | 统一导出 | 60 分钟 |
            ## 时间线
            - `09:00` 开发
            ## 站会视图
            - **昨天完成**：导出
            - **今天计划**：测试
            - **阻塞项**：无
            """;

        var sections = RangeReportMarkdownSections.Parse(markdown);
        Assert.Contains("完成报告导出格式统一", sections.Summary, StringComparison.Ordinal);
        Assert.Single(sections.Highlights);
        var project = Assert.Single(sections.Projects);
        Assert.Equal("AiAssistant", project.Name);
        Assert.Equal("统一导出", project.Summary);
        Assert.Equal("60 分钟", project.Duration);
        Assert.Single(sections.Timeline);
        Assert.Equal(3, sections.Standup.Count);

        var report = CreateReport(days: 1) with { Markdown = markdown };
        var pdf = new SkiaReportRasterizer().RenderPdf(report);
        Assert.True(pdf.AsSpan(0, 4).SequenceEqual("%PDF"u8));
        Assert.True(pdf.Length < 1_000_000, $"pdf unexpectedly large (font embedding?): {pdf.Length}");
        if (PdfSharpReportRenderer.IsSearchableFontAvailable)
        {
            var ascii = Encoding.ASCII.GetString(pdf);
            Assert.Contains("/ToUnicode", ascii, StringComparison.Ordinal);
            Assert.True(Regex.Count(ascii, @"/Type\s*/Page\b") >= 1);
        }
    }

    [Fact]
    public void PdfWithoutText_DoesNotEmbedFonts()
    {
        using var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream))
        {
            var canvas = document.BeginPage(960, 420);
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(10, 10, 100, 100, paint);
            document.EndPage();
            document.Close();
        }

        Assert.True(stream.Length < 100_000, $"pdf length {stream.Length}");
    }

    [Fact]
    public void EmptyReport_RendersWithoutThrowing()
    {
        var rasterizer = new SkiaReportRasterizer();
        var report = CreateReport(days: 7) with { DailyStats = Array.Empty<DailyActivityStat>() };

        Assert.NotEmpty(rasterizer.RenderPng(report));
        Assert.NotEmpty(rasterizer.RenderPdf(report));
    }

    private static RangeReport CreateReport(int days)
    {
        var stats = Enumerable.Range(0, days)
            .Select(index => new DailyActivityStat(
                new DateOnly(2026, 11, 16).AddDays(index),
                index == 0 ? 5 : index,
                30 + index,
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Rider"] = index + 1 }))
            .ToArray();
        return new RangeReport(
            new DateOnly(2026, 11, 16),
            new DateOnly(2026, 11, 16).AddDays(days - 1),
            "# report",
            "{}",
            stats,
            "gemma4:12b",
            false,
            null);
    }
}
