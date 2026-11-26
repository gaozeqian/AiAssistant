using System.Globalization;
using System.Text;
using AiAssistant.Application.Ports;
using SkiaSharp;

namespace AiAssistant.Export;

/// <summary>
/// Renders structured Q&amp;A, diagnostics and benchmark documents to SVG/PNG/PDF with SkiaSharp.
/// Text is laid out once and shared by all three formats so the exports never disagree.
/// </summary>
public sealed class SkiaReportDocumentRenderer : IReportDocumentRenderer
{
    private const int PageWidth = 960;
    private const int PageHeight = 1240;
    private const int Margin = 48;
    private const int ContentWidth = PageWidth - (2 * Margin);
    private const int PageBottom = PageHeight - 76;
    private const int FooterBaseline = PageHeight - 30;

    private const string TitleColor = "#111C36";
    private const string SubtitleColor = "#64748B";
    private const string HeadingColor = "#1E293B";
    private const string BodyColor = "#334155";
    private const string MutedColor = "#475569";
    private const string FooterColor = "#94A3B8";

    public string RenderSvg(ReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var pages = CreateLayout(document);
        var totalHeight = pages.Count * PageHeight;
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{PageWidth}\" height=\"{totalHeight}\" viewBox=\"0 0 {PageWidth} {totalHeight}\" role=\"img\" aria-label=\"{Escape(document.Title)}\">");
        builder.AppendLine("  <rect width=\"100%\" height=\"100%\" fill=\"#FFFFFF\"/>");
        for (var index = 0; index < pages.Count; index++)
        {
            var offset = index * PageHeight;
            builder.AppendLine(CultureInfo.InvariantCulture, $"  <g transform=\"translate(0,{offset})\">");
            foreach (var line in pages[index].Lines)
            {
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"    <text x=\"{line.X:F1}\" y=\"{line.Baseline:F1}\" font-family=\"Segoe UI, PingFang SC, sans-serif\" font-size=\"{line.FontSize:F1}\"{(line.Bold ? " font-weight=\"600\"" : string.Empty)} fill=\"{line.Color}\">{Escape(line.Text)}</text>");
            }

            AppendSvgFooter(builder, document, index + 1, pages.Count);
            builder.AppendLine("  </g>");
        }

        builder.AppendLine("</svg>");
        return builder.ToString();
    }

    public byte[] RenderPng(ReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var pages = CreateLayout(document);
        const int scale = 2;
        var height = pages.Count * PageHeight;
        using var surface = SKSurface.Create(new SKImageInfo(
            PageWidth * scale,
            height * scale,
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        canvas.Scale(scale);
        var typeface = ResolveTypeface();
        try
        {
            for (var index = 0; index < pages.Count; index++)
            {
                canvas.Save();
                canvas.Translate(0, index * PageHeight);
                DrawPage(canvas, document, pages[index], index + 1, pages.Count, typeface, vectorText: false);
                canvas.Restore();
            }
        }
        finally
        {
            DisposeTypeface(typeface);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public byte[] RenderPdf(ReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var pages = CreateLayout(document);
        using var stream = new MemoryStream();
        using (var pdf = SKDocument.CreatePdf(stream))
        {
            var typeface = ResolveTypeface();
            try
            {
                for (var index = 0; index < pages.Count; index++)
                {
                    var canvas = pdf.BeginPage(PageWidth, PageHeight);
                    canvas.Clear(SKColors.White);
                    DrawPage(canvas, document, pages[index], index + 1, pages.Count, typeface, vectorText: true);
                    pdf.EndPage();
                }
            }
            finally
            {
                DisposeTypeface(typeface);
            }

            pdf.Close();
        }

        return stream.ToArray();
    }

    private static List<LayoutPage> CreateLayout(ReportDocument document)
    {
        var pages = new List<LayoutPage>();
        var lines = new List<LayoutLine>();
        var y = (float)Margin;

        void AddLine(string? text, float fontSize, bool bold, string color, float indent = 0)
        {
            var maxWidth = ContentWidth - indent;
            foreach (var wrapped in Wrap(text ?? string.Empty, fontSize, maxWidth))
            {
                var lineHeight = fontSize * 1.7f;
                if (y + lineHeight > PageBottom)
                {
                    pages.Add(new LayoutPage(lines));
                    lines = [];
                    y = Margin;
                }

                lines.Add(new LayoutLine(wrapped, Margin + indent, y + fontSize, fontSize, bold, color));
                y += lineHeight;
            }
        }

        AddLine(document.Title, 26, bold: true, TitleColor);
        if (!string.IsNullOrWhiteSpace(document.Subtitle))
        {
            AddLine(document.Subtitle, 13, bold: false, SubtitleColor);
        }

        y += 8;
        foreach (var section in document.Sections)
        {
            y += 10;
            AddLine(section.Heading, 16, bold: true, HeadingColor);
            y += 4;
            switch (section)
            {
                case ReportParagraphSection paragraphs:
                    foreach (var paragraph in paragraphs.Paragraphs)
                    {
                        AddLine(paragraph, 12, bold: false, BodyColor);
                        y += 3;
                    }

                    break;
                case ReportBulletSection bullets:
                    foreach (var item in bullets.Items)
                    {
                        AddLine($"· {item}", 12, bold: false, BodyColor, indent: 12);
                    }

                    break;
                case ReportFieldSection fields:
                    foreach (var field in fields.Fields)
                    {
                        AddLine($"{field.Label}：{field.Value}", 12, bold: false, BodyColor);
                    }

                    break;
                case ReportCodeSection code:
                    foreach (var line in code.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                    {
                        AddLine(line, 10.5f, bold: false, MutedColor, indent: 12);
                    }

                    break;
            }

            y += 4;
        }

        if (lines.Count > 0 || pages.Count == 0)
        {
            pages.Add(new LayoutPage(lines));
        }

        return pages;
    }

    private static IEnumerable<string> Wrap(string text, float fontSize, float maxWidth)
    {
        var maxUnits = Math.Max(8f, maxWidth / Math.Max(1f, fontSize));
        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (rawLine.Length == 0)
            {
                yield return string.Empty;
                continue;
            }

            var builder = new StringBuilder();
            var units = 0f;
            foreach (var character in rawLine)
            {
                var width = CharacterUnits(character);
                if (units + width > maxUnits && builder.Length > 0)
                {
                    yield return builder.ToString().TrimEnd();
                    builder.Clear();
                    units = 0f;
                    if (char.IsWhiteSpace(character))
                    {
                        continue;
                    }
                }

                builder.Append(character);
                units += width;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString().TrimEnd();
            }
        }
    }

    private static float CharacterUnits(char character) =>
        character >= 0x2E80 ? 1.0f : character <= 0x7F ? 0.55f : 0.65f;

    private static void DrawPage(
        SKCanvas canvas,
        ReportDocument document,
        LayoutPage page,
        int pageNumber,
        int pageCount,
        SKTypeface typeface,
        bool vectorText)
    {
        foreach (var line in page.Lines)
        {
            using var font = new SKFont(typeface, line.FontSize)
            {
                Embolden = line.Bold
            };
            using var paint = new SKPaint
            {
                Color = SKColor.Parse(line.Color),
                IsAntialias = true
            };
            SkiaReportRasterizer.DrawText(
                canvas,
                line.Text,
                line.X,
                line.Baseline,
                SKTextAlign.Left,
                font,
                paint,
                vectorText);
        }

        using var footerFont = new SKFont(typeface, 10);
        using var footerPaint = new SKPaint
        {
            Color = SKColor.Parse(FooterColor),
            IsAntialias = true
        };
        SkiaReportRasterizer.DrawText(
            canvas,
            document.Footer,
            Margin,
            FooterBaseline,
            SKTextAlign.Left,
            footerFont,
            footerPaint,
            vectorText);
        SkiaReportRasterizer.DrawText(
            canvas,
            string.Create(CultureInfo.InvariantCulture, $"第 {pageNumber} / {pageCount} 页"),
            PageWidth - Margin,
            FooterBaseline,
            SKTextAlign.Right,
            footerFont,
            footerPaint,
            vectorText);
    }

    private static void AppendSvgFooter(StringBuilder builder, ReportDocument document, int pageNumber, int pageCount)
    {
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"    <text x=\"{Margin}\" y=\"{FooterBaseline}\" font-family=\"Segoe UI, PingFang SC, sans-serif\" font-size=\"10\" fill=\"{FooterColor}\">{Escape(document.Footer)}</text>");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"    <text x=\"{PageWidth - Margin}\" y=\"{FooterBaseline}\" text-anchor=\"end\" font-family=\"Segoe UI, PingFang SC, sans-serif\" font-size=\"10\" fill=\"{FooterColor}\">第 {pageNumber} / {pageCount} 页</text>");
    }

    private static SKTypeface ResolveTypeface() =>
        SKFontManager.Default.MatchCharacter('时') ?? SKTypeface.Default;

    private static void DisposeTypeface(SKTypeface typeface)
    {
        if (!ReferenceEquals(typeface, SKTypeface.Default))
        {
            typeface.Dispose();
        }
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);

    private sealed record LayoutLine(string Text, float X, float Baseline, float FontSize, bool Bold, string Color);

    private sealed record LayoutPage(IReadOnlyList<LayoutLine> Lines);
}
