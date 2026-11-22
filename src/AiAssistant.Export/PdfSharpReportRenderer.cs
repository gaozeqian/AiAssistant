using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using SkiaSharp;

namespace AiAssistant.Export;

/// <summary>
/// Searchable PDF renderer using PDFsharp with a system TrueType CJK font and font subsetting.
/// Falls back to the Skia vector-outline renderer when no suitable TrueType font is available.
/// </summary>
public static class PdfSharpReportRenderer
{
    private const string FamilyName = "AiAssistantCjk";
    private const int Width = 960;
    private const int Height = 420;
    private const int ChartLeft = 60;
    private const int ChartRight = 900;
    private const int ChartTop = 110;
    private const int ChartBottom = 330;

    private static readonly object ResolverSync = new();
    private static readonly Lazy<string?> FontPath = new(FindFont, LazyThreadSafetyMode.ExecutionAndPublication);
    private static bool _resolverInitialized;

    public static bool IsSearchableFontAvailable => FontPath.Value is not null;

    public static string? ResolvedFontPath => FontPath.Value;

    public static bool TryRender(RangeReport report, out byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(report);
        pdf = [];
        if (!EnsureResolver())
        {
            return false;
        }

        try
        {
            using var document = new PdfDocument();
            document.Info.Title = "AiAssistant 区间活动报告";
            document.Info.Creator = "AiAssistant";
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(Width);
            page.Height = XUnit.FromPoint(Height);
            using var graphics = XGraphics.FromPdfPage(page);
            Draw(graphics, report);
            using var stream = new MemoryStream();
            document.Save(stream, closeStream: false);
            pdf = stream.ToArray();
            return pdf.Length > 0;
        }
        catch (Exception ex) when (ex is PdfSharpException or IOException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool EnsureResolver()
    {
        var path = FontPath.Value;
        if (path is null)
        {
            return false;
        }

        lock (ResolverSync)
        {
            if (_resolverInitialized)
            {
                return true;
            }

            GlobalFontSettings.FontResolver = new SingleFontResolver(path);
            _resolverInitialized = true;
            return true;
        }
    }

    private static void Draw(XGraphics graphics, RangeReport report)
    {
        var fontOptions = new XPdfFontOptions(PdfFontEncoding.Unicode, PdfFontEmbedding.TryComputeSubset);
        var titleFont = new XFont(FamilyName, 24, XFontStyleEx.Regular, fontOptions);
        var subtitleFont = new XFont(FamilyName, 13, XFontStyleEx.Regular, fontOptions);
        var labelFont = new XFont(FamilyName, 11, XFontStyleEx.Regular, fontOptions);
        var smallFont = new XFont(FamilyName, 10, XFontStyleEx.Regular, fontOptions);
        var titleBrush = new XSolidBrush(XColor.FromArgb(0x11, 0x1C, 0x36));
        var mutedBrush = new XSolidBrush(XColor.FromArgb(0x64, 0x74, 0x8B));
        var labelBrush = new XSolidBrush(XColor.FromArgb(0x47, 0x55, 0x69));
        var smallBrush = new XSolidBrush(XColor.FromArgb(0x94, 0xA3, 0xB8));
        var accentBrush = new XSolidBrush(XColor.FromArgb(0x31, 0x57, 0xD5));
        var gridPen = new XPen(XColor.FromArgb(0xE2, 0xE8, 0xF0), 1);
        var axisPen = new XPen(XColor.FromArgb(0x94, 0xA3, 0xB8), 1);

        graphics.DrawString(Title(report), titleFont, titleBrush, new XPoint(48, 48), XStringFormats.BaseLineLeft);
        graphics.DrawString(Subtitle(report), subtitleFont, mutedBrush, new XPoint(48, 76), XStringFormats.BaseLineLeft);

        if (report.DailyStats.Count == 0 || report.TotalEvents == 0)
        {
            graphics.DrawString(
                "该区间没有可用的本地活动记录",
                subtitleFont,
                mutedBrush,
                new XPoint(Width / 2.0, 220),
                XStringFormats.BaseLineCenter);
        }
        else
        {
            DrawChart(
                graphics,
                report,
                labelFont,
                smallFont,
                labelBrush,
                smallBrush,
                accentBrush,
                gridPen,
                axisPen);
        }

        graphics.DrawString(
            "由 AiAssistant 本地生成 · 数据已脱敏",
            smallFont,
            smallBrush,
            new XPoint(48, 400),
            XStringFormats.BaseLineLeft);
    }

    private static void DrawChart(
        XGraphics graphics,
        RangeReport report,
        XFont labelFont,
        XFont smallFont,
        XBrush labelBrush,
        XBrush smallBrush,
        XBrush accentBrush,
        XPen gridPen,
        XPen axisPen)
    {
        var maxEvents = Math.Max(1, report.MaxDailyEvents);
        var intervals = ChartAxis.Intervals(maxEvents);
        for (var step = 0; step <= intervals; step++)
        {
            var ratio = step / (double)intervals;
            var y = ChartBottom - ((ChartBottom - ChartTop) * ratio);
            graphics.DrawLine(gridPen, ChartLeft, y, ChartRight, y);
            graphics.DrawString(
                ChartAxis.Label(maxEvents, step, intervals),
                smallFont,
                smallBrush,
                new XPoint(ChartLeft - 10, y + 4),
                XStringFormats.BaseLineRight);
        }

        var count = report.DailyStats.Count;
        var slotWidth = (ChartRight - ChartLeft) / (double)count;
        var barWidth = Math.Min(56, slotWidth * 0.55);
        for (var index = 0; index < count; index++)
        {
            var day = report.DailyStats[index];
            var ratio = day.EventCount / (double)maxEvents;
            var barHeight = (ChartBottom - ChartTop) * ratio;
            var x = ChartLeft + (slotWidth * index) + ((slotWidth - barWidth) / 2);
            var y = ChartBottom - barHeight;
            if (barHeight > 0)
            {
                graphics.DrawRoundedRectangle(
                    accentBrush,
                    new XRect(x, y, barWidth, barHeight),
                    new XSize(4, 4));
            }

            var center = x + (barWidth / 2);
            graphics.DrawString(
                day.EventCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                labelFont,
                accentBrush,
                new XPoint(center, Math.Max(ChartTop - 6, y - 8)),
                XStringFormats.BaseLineCenter);
            graphics.DrawString(
                day.Date.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                labelFont,
                labelBrush,
                new XPoint(center, ChartBottom + 22),
                XStringFormats.BaseLineCenter);
            graphics.DrawString(
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{day.ActiveMinutes} 分钟"),
                smallFont,
                smallBrush,
                new XPoint(center, ChartBottom + 40),
                XStringFormats.BaseLineCenter);
        }

        graphics.DrawLine(axisPen, ChartLeft, ChartBottom, ChartRight, ChartBottom);
    }

    private static string Title(RangeReport report) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{report.FromDate:yyyy年MM月dd日} – {report.ToDate:yyyy年MM月dd日} 活动报告");

    private static string Subtitle(RangeReport report) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"共 {report.TotalEvents} 条事件 · 活跃约 {report.TotalActiveMinutes} 分钟 · 模型 {report.ModelId}{(report.Degraded ? "（统计模式）" : string.Empty)}");

    private static string? FindFont()
    {
        var configured = Environment.GetEnvironmentVariable("AIASSISTANT_PDF_FONT_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        string[] candidates = OperatingSystem.IsWindows()
            ?
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "simhei.ttf"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "Deng.ttf")
            ]
            : OperatingSystem.IsMacOS()
                ?
                [
                    "/System/Library/Fonts/Supplemental/Arial Unicode.ttf",
                    "/Library/Fonts/Arial Unicode.ttf"
                ]
                :
                [
                    "/usr/share/fonts/truetype/noto/NotoSansSC-Regular.ttf",
                    "/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttf",
                    "/usr/share/fonts/truetype/arphic/uming.ttf",
                    "/usr/share/fonts/opentype/noto/NotoSansSC-Regular.otf"
                ];

        var explicitMatch = candidates.FirstOrDefault(path =>
            File.Exists(path) &&
            (path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)));
        return explicitMatch ?? ScanForCjkTrueType();
    }

    /// <summary>Fallback discovery for Linux/macOS where font names vary by distribution.</summary>
    private static string? ScanForCjkTrueType()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots = OperatingSystem.IsWindows()
            ? [Environment.GetFolderPath(Environment.SpecialFolder.Fonts)]
            : OperatingSystem.IsMacOS()
                ?
                [
                    "/System/Library/Fonts",
                    "/Library/Fonts",
                    Path.Combine(home, "Library", "Fonts")
                ]
                :
                [
                    "/usr/share/fonts",
                    "/usr/local/share/fonts",
                    Path.Combine(home, ".local", "share", "fonts")
                ];

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.ttf", SearchOption.AllDirectories).Take(500);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (file.Contains("VF", StringComparison.OrdinalIgnoreCase) &&
                    file.Contains("NotoSans", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (SupportsCjk(file))
                {
                    return file;
                }
            }
        }

        return null;
    }

    private static bool SupportsCjk(string path)
    {
        try
        {
            using var typeface = SKTypeface.FromFile(path);
            return typeface is not null && typeface.GetGlyph('时') > 0 && typeface.GetGlyph('报') > 0;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return false;
        }
    }

    private sealed class SingleFontResolver(string path) : IFontResolver
    {
        private readonly byte[] _bytes = File.ReadAllBytes(path);

        public byte[]? GetFont(string faceName) =>
            string.Equals(faceName, FamilyName, StringComparison.Ordinal) ? _bytes : null;

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
            string.Equals(familyName, FamilyName, StringComparison.Ordinal)
                ? new FontResolverInfo(FamilyName)
                : null;
    }
}
