using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using SkiaSharp;

namespace AiAssistant.Export;

/// <summary>
/// Renders range reports to PNG and PDF with SkiaSharp (already shipped as an Avalonia dependency).
/// Uses a CJK-capable system typeface when available; falls back to the platform default.
/// </summary>
public sealed class SkiaReportRasterizer : IReportRasterizer
{
    private const int Width = 960;
    private const int Height = 420;
    private const int ChartLeft = 60;
    private const int ChartRight = 900;
    private const int ChartTop = 110;
    private const int ChartBottom = 330;

    private static readonly SKColor Background = new(0xFF, 0xFF, 0xFF);
    private static readonly SKColor Accent = new(0x31, 0x57, 0xD5);
    private static readonly SKColor TitleColor = new(0x11, 0x1C, 0x36);
    private static readonly SKColor MutedColor = new(0x64, 0x74, 0x8B);
    private static readonly SKColor GridColor = new(0xE2, 0xE8, 0xF0);
    private static readonly SKColor AxisColor = new(0x94, 0xA3, 0xB8);
    private static readonly SKColor LabelColor = new(0x47, 0x55, 0x69);

    public byte[] RenderPng(RangeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        const int scale = 2;
        using var surface = SKSurface.Create(new SKImageInfo(
            Width * scale,
            Height * scale,
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(Background);
        canvas.Scale(scale);
        Draw(canvas, report, vectorText: false);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public byte[] RenderPdf(RangeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (PdfSharpReportRenderer.TryRender(report, out var searchable))
        {
            return searchable;
        }

        return RenderVectorPdf(report);
    }

    private static byte[] RenderVectorPdf(RangeReport report)
    {
        using var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream))
        {
            var canvas = document.BeginPage(Width, Height);
            Draw(canvas, report, vectorText: true);
            document.EndPage();

            // No embeddable CJK TrueType font: be explicit that the detailed pages are unavailable.
            canvas = document.BeginPage(Width, Height);
            DrawDegradedNotice(canvas);
            document.EndPage();
            document.Close();
        }

        return stream.ToArray();
    }

    private static void DrawDegradedNotice(SKCanvas canvas)
    {
        using var matched = SKFontManager.Default.MatchCharacter('时');
        using var font = new SKFont(matched ?? SKTypeface.Default, 14);
        using var paint = new SKPaint { Color = MutedColor, IsAntialias = true };
        DrawText(canvas, "未找到可嵌入的中文字体，PDF 已降级为仅图表。", 48, 70, SKTextAlign.Left, font, paint, true);
        DrawText(canvas, "如需完整正文，请导出 Markdown 版本。", 48, 104, SKTextAlign.Left, font, paint, true);
    }

    private static void Draw(SKCanvas canvas, RangeReport report, bool vectorText)
    {
        using var matchedTypeface = SKFontManager.Default.MatchCharacter('时');
        var typeface = matchedTypeface ?? SKTypeface.Default;
        using var titleFont = new SKFont(typeface, 24);
        using var subtitleFont = new SKFont(typeface, 13);
        using var labelFont = new SKFont(typeface, 11);
        using var smallFont = new SKFont(typeface, 10);
        using var titlePaint = new SKPaint { Color = TitleColor, IsAntialias = true };
        using var mutedPaint = new SKPaint { Color = MutedColor, IsAntialias = true };
        using var labelPaint = new SKPaint { Color = LabelColor, IsAntialias = true };
        using var smallPaint = new SKPaint { Color = AxisColor, IsAntialias = true };
        using var accentPaint = new SKPaint { Color = Accent, IsAntialias = true };
        using var gridPaint = new SKPaint
        {
            Color = GridColor,
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };
        using var axisPaint = new SKPaint
        {
            Color = AxisColor,
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        DrawText(canvas, Title(report), 48, 48, SKTextAlign.Left, titleFont, titlePaint, vectorText);
        DrawText(canvas, Subtitle(report), 48, 76, SKTextAlign.Left, subtitleFont, mutedPaint, vectorText);

        if (report.DailyStats.Count == 0 || report.TotalEvents == 0)
        {
            DrawText(canvas, "该区间没有可用的本地活动记录", Width / 2f, 220, SKTextAlign.Center, subtitleFont, mutedPaint, vectorText);
        }
        else
        {
            DrawChart(canvas, report, labelFont, smallFont, labelPaint, smallPaint, accentPaint, gridPaint, axisPaint, vectorText);
        }

        DrawText(canvas, "由 AiAssistant 本地生成 · 数据已脱敏", 48, 400, SKTextAlign.Left, smallFont, smallPaint, vectorText);
    }

    private static void DrawChart(
        SKCanvas canvas,
        RangeReport report,
        SKFont labelFont,
        SKFont smallFont,
        SKPaint labelPaint,
        SKPaint smallPaint,
        SKPaint accentPaint,
        SKPaint gridPaint,
        SKPaint axisPaint,
        bool vectorText)
    {
        var maxEvents = Math.Max(1, report.MaxDailyEvents);
        var intervals = ChartAxis.Intervals(maxEvents);
        for (var step = 0; step <= intervals; step++)
        {
            var ratio = step / (float)intervals;
            var y = ChartBottom - ((ChartBottom - ChartTop) * ratio);
            canvas.DrawLine(ChartLeft, y, ChartRight, y, gridPaint);
            DrawText(
                canvas,
                ChartAxis.Label(maxEvents, step, intervals),
                ChartLeft - 10,
                y + 4,
                SKTextAlign.Right,
                smallFont,
                smallPaint,
                vectorText);
        }

        var count = report.DailyStats.Count;
        var slotWidth = (ChartRight - ChartLeft) / (float)count;
        var barWidth = Math.Min(56, slotWidth * 0.55f);
        for (var index = 0; index < count; index++)
        {
            var day = report.DailyStats[index];
            var ratio = day.EventCount / (float)maxEvents;
            var barHeight = (ChartBottom - ChartTop) * ratio;
            var x = ChartLeft + (slotWidth * index) + ((slotWidth - barWidth) / 2);
            var y = ChartBottom - barHeight;
            if (barHeight > 0)
            {
                canvas.DrawRoundRect(new SKRect(x, y, x + barWidth, ChartBottom), 4, 4, accentPaint);
            }

            var center = x + (barWidth / 2);
            DrawText(
                canvas,
                day.EventCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                center,
                Math.Max(ChartTop - 6, y - 8),
                SKTextAlign.Center,
                labelFont,
                accentPaint,
                vectorText);
            DrawText(
                canvas,
                day.Date.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                center,
                ChartBottom + 22,
                SKTextAlign.Center,
                labelFont,
                labelPaint,
                vectorText);
            DrawText(
                canvas,
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{day.ActiveMinutes} 分钟"),
                center,
                ChartBottom + 40,
                SKTextAlign.Center,
                smallFont,
                smallPaint,
                vectorText);
        }

        canvas.DrawLine(ChartLeft, ChartBottom, ChartRight, ChartBottom, axisPaint);
    }

    /// <summary>
    /// PDF output draws glyph outlines instead of text runs, avoiding multi-megabyte CJK font embedding.
    /// PNG keeps normal text rendering for better hinting.
    /// </summary>
    private static void DrawText(
        SKCanvas canvas,
        string text,
        float x,
        float y,
        SKTextAlign align,
        SKFont font,
        SKPaint paint,
        bool vectorText)
    {
        if (!vectorText)
        {
            canvas.DrawText(text, x, y, align, font, paint);
            return;
        }

        var glyphs = font.GetGlyphs(text);
        if (glyphs.Length == 0)
        {
            return;
        }

        var widths = font.GetGlyphWidths(glyphs);
        var total = widths.Sum();
        var offset = align switch
        {
            SKTextAlign.Center => x - (total / 2),
            SKTextAlign.Right => x - total,
            _ => x
        };

        using var path = new SKPath();
        for (var index = 0; index < glyphs.Length; index++)
        {
            using var glyphPath = font.GetGlyphPath(glyphs[index]);
            if (glyphPath is not null)
            {
                // Re-parse the outline so the PDF backend cannot associate it with the source typeface.
                using var detached = SKPath.ParseSvgPathData(glyphPath.ToSvgPathData());
                if (detached is not null)
                {
                    path.AddPath(detached, offset, y);
                }
            }

            offset += widths[index];
        }

        canvas.DrawPath(path, paint);
    }

    private static string Title(RangeReport report) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{report.FromDate:yyyy年MM月dd日} – {report.ToDate:yyyy年MM月dd日} 活动报告");

    private static string Subtitle(RangeReport report) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"共 {report.TotalEvents} 条事件 · 活跃约 {report.TotalActiveMinutes} 分钟 · 模型 {report.ModelId}{(report.Degraded ? "（统计模式）" : string.Empty)}");
}
