using System.Text;
using AiAssistant.Application.Ports;
using AiAssistant.Export;
using SkiaSharp;

namespace AiAssistant.Report.Tests;

/// <summary>PNG/PDF rasterization uses the SkiaSharp build already shipped with Avalonia.</summary>
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
        Assert.True(pdf.AsSpan(0, 4).SequenceEqual("%PDF"u8));
        Assert.Contains("%%EOF", Encoding.ASCII.GetString(pdf), StringComparison.Ordinal);
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
