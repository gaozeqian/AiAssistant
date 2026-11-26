using System.Globalization;
using System.Text;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.Configuration;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using SkiaSharp;

namespace AiAssistant.Export;

/// <summary>
/// Searchable multi-page PDF renderer using PDFsharp with a system TrueType CJK font and font
/// subsetting. Falls back to the Skia vector-outline renderer when no suitable font is available.
/// </summary>
public static class PdfSharpReportRenderer
{
    private const string FamilyName = "AiAssistantCjk";

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
            new DocumentWriter(document).Render(report);
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

    private static string Title(RangeReport report) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{report.FromDate:yyyy年MM月dd日} – {report.ToDate:yyyy年MM月dd日} 活动报告");

    private static string Subtitle(RangeReport report) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"共 {report.TotalEvents} 条事件 · 活跃约 {report.TotalActiveMinutes} 分钟 · 模型 {report.ModelId}{(report.Degraded ? "（统计模式）" : string.Empty)}");

    private static string? FindFont()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentNames.PdfFontPath);
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

    /// <summary>A4 multi-page layout: overview + chart, then the detailed tables and records.</summary>
    private sealed class DocumentWriter
    {
        private const double PageWidth = 595.28;
        private const double PageHeight = 841.89;
        private const double Margin = 44;
        private const double ContentWidth = PageWidth - (2 * Margin);

        private static readonly XColor TitleColor = XColor.FromArgb(0x11, 0x1C, 0x36);
        private static readonly XColor SectionColor = XColor.FromArgb(0x1E, 0x29, 0x3B);
        private static readonly XColor BodyColor = XColor.FromArgb(0x33, 0x41, 0x55);
        private static readonly XColor MutedColor = XColor.FromArgb(0x64, 0x74, 0x8B);
        private static readonly XColor AccentColor = XColor.FromArgb(0x31, 0x57, 0xD5);
        private static readonly XColor SubtleColor = XColor.FromArgb(0xF1, 0xF5, 0xFA);
        private static readonly XColor GridColor = XColor.FromArgb(0xE2, 0xE8, 0xF0);

        private readonly PdfDocument _document;
        private readonly XPdfFontOptions _fontOptions =
            new(PdfFontEncoding.Unicode, PdfFontEmbedding.TryComputeSubset);
        private readonly XFont _titleFont;
        private readonly XFont _sectionFont;
        private readonly XFont _bodyFont;
        private readonly XFont _smallFont;
        private readonly XFont _kpiFont;
        private readonly XPen _gridPen = new(GridColor, 0.6);
        private readonly XPen _axisPen = new(MutedColor, 0.8);
        private readonly XPen _accentPen = new(AccentColor, 1.2);

        private XGraphics _graphics = null!;
        private double _y;
        private int _pageNumber;

        public DocumentWriter(PdfDocument document)
        {
            _document = document;
            _titleFont = new XFont(FamilyName, 17, XFontStyleEx.Regular, _fontOptions);
            _sectionFont = new XFont(FamilyName, 12, XFontStyleEx.Regular, _fontOptions);
            _bodyFont = new XFont(FamilyName, 10, XFontStyleEx.Regular, _fontOptions);
            _smallFont = new XFont(FamilyName, 8.5, XFontStyleEx.Regular, _fontOptions);
            _kpiFont = new XFont(FamilyName, 14, XFontStyleEx.Regular, _fontOptions);
        }

        public void Render(RangeReport report)
        {
            var sections = RangeReportMarkdownSections.Parse(report.Markdown);
            BeginPage();
            DrawTitle(report);
            DrawKpis(report);

            DrawHeading("活动概览");
            DrawParagraph(string.IsNullOrWhiteSpace(sections.Summary)
                ? "该区间没有可用的总结文本。"
                : sections.Summary);

            DrawHeading("每日活动");
            if (report.DailyStats.Count > 0 && report.TotalEvents > 0)
            {
                DrawChart(report);
            }
            else
            {
                DrawParagraph("该区间没有可用的本地活动记录。");
            }

            if (report.DailyStats.Count > 0)
            {
                DrawHeading("每日统计");
                DrawDailyTable(report.DailyStats);
            }

            if (sections.Apps.Count > 0)
            {
                DrawHeading("应用分布");
                DrawApps(sections.Apps);
            }

            if (sections.Highlights.Count > 0)
            {
                DrawHeading("代表性记录");
                DrawHighlights(sections.Highlights);
            }

            _graphics.Dispose();
        }

        private void BeginPage()
        {
            _pageNumber++;
            var page = _document.AddPage();
            page.Width = XUnit.FromPoint(PageWidth);
            page.Height = XUnit.FromPoint(PageHeight);
            _graphics = XGraphics.FromPdfPage(page);
            _y = Margin;

            var muted = new XSolidBrush(MutedColor);
            _graphics.DrawString(
                "由 AiAssistant 本地生成 · 数据已脱敏",
                _smallFont,
                muted,
                new XPoint(Margin, PageHeight - 24),
                XStringFormats.BaseLineLeft);
            _graphics.DrawString(
                $"第 {_pageNumber} 页",
                _smallFont,
                muted,
                new XPoint(PageWidth - Margin, PageHeight - 24),
                XStringFormats.BaseLineRight);
        }

        private void EnsureSpace(double needed)
        {
            if (_y + needed <= PageHeight - Margin - 18)
            {
                return;
            }

            _graphics.Dispose();
            BeginPage();
        }

        private void DrawTitle(RangeReport report)
        {
            var title = new XSolidBrush(TitleColor);
            var muted = new XSolidBrush(MutedColor);
            _graphics.DrawString(Title(report), _titleFont, title, new XPoint(Margin, _y), XStringFormats.BaseLineLeft);
            _y += 24;
            _graphics.DrawString(Subtitle(report), _smallFont, muted, new XPoint(Margin, _y), XStringFormats.BaseLineLeft);
            _y += 14;
            _graphics.DrawLine(_accentPen, Margin, _y, PageWidth - Margin, _y);
            _y += 18;
        }

        private void DrawKpis(RangeReport report)
        {
            var items = new[]
            {
                (report.TotalEvents.ToString(CultureInfo.InvariantCulture), "条事件"),
                (report.TotalActiveMinutes.ToString(CultureInfo.InvariantCulture), "活跃分钟"),
                (report.DailyStats.Count(day => day.EventCount > 0).ToString(CultureInfo.InvariantCulture), "活跃天数")
            };
            var gap = 10;
            var width = (ContentWidth - (2 * gap)) / 3;

            var subtle = new XSolidBrush(SubtleColor);
            var accent = new XSolidBrush(AccentColor);
            var muted = new XSolidBrush(MutedColor);
            for (var index = 0; index < items.Length; index++)
            {
                var rect = new XRect(Margin + (index * (width + gap)), _y, width, 52);
                _graphics.DrawRoundedRectangle(subtle, rect, new XSize(6, 6));
                _graphics.DrawString(
                    items[index].Item1,
                    _kpiFont,
                    accent,
                    new XPoint(rect.X + 12, rect.Y + 25),
                    XStringFormats.BaseLineLeft);
                _graphics.DrawString(
                    items[index].Item2,
                    _smallFont,
                    muted,
                    new XPoint(rect.X + 12, rect.Y + 44),
                    XStringFormats.BaseLineLeft);
            }

            _y += 64;
        }

        private void DrawHeading(string text)
        {
            EnsureSpace(30);
            _y += 8;
            var brush = new XSolidBrush(SectionColor);
            _graphics.DrawString(text, _sectionFont, brush, new XPoint(Margin, _y), XStringFormats.BaseLineLeft);
            _y += 14;
            _graphics.DrawLine(_gridPen, Margin, _y, PageWidth - Margin, _y);
            _y += 8;
        }

        private void DrawParagraph(string text)
        {
            var brush = new XSolidBrush(BodyColor);
            foreach (var line in Wrap(text, _bodyFont, ContentWidth))
            {
                EnsureSpace(16);
                _graphics.DrawString(line, _bodyFont, brush, new XPoint(Margin, _y), XStringFormats.BaseLineLeft);
                _y += 15;
            }

            _y += 4;
        }

        private void DrawChart(RangeReport report)
        {
            const double chartHeight = 160;
            EnsureSpace(chartHeight + 48);

            var left = Margin + 26;
            var right = PageWidth - Margin;
            var top = _y + 12;
            var bottom = top + chartHeight;
            var maxEvents = Math.Max(1, report.MaxDailyEvents);
            var intervals = ChartAxis.Intervals(maxEvents);

            var muted = new XSolidBrush(MutedColor);
            var accent = new XSolidBrush(AccentColor);
            var label = new XSolidBrush(BodyColor);
            for (var step = 0; step <= intervals; step++)
            {
                var ratio = step / (double)intervals;
                var y = bottom - (chartHeight * ratio);
                _graphics.DrawLine(_gridPen, left, y, right, y);
                _graphics.DrawString(
                    ChartAxis.Label(maxEvents, step, intervals),
                    _smallFont,
                    muted,
                    new XPoint(left - 6, y + 3),
                    XStringFormats.BaseLineRight);
            }

            var count = report.DailyStats.Count;
            var slotWidth = (right - left) / count;
            var barWidth = Math.Min(28, slotWidth * 0.55);
            var labelStep = count > 14 ? (int)Math.Ceiling(count / 12d) : 1;
            for (var index = 0; index < count; index++)
            {
                var day = report.DailyStats[index];
                var barHeight = chartHeight * (day.EventCount / (double)maxEvents);
                var x = left + (slotWidth * index) + ((slotWidth - barWidth) / 2);
                if (barHeight > 0)
                {
                    _graphics.DrawRoundedRectangle(
                        accent,
                        new XRect(x, bottom - barHeight, barWidth, barHeight),
                        new XSize(3, 3));
                }

                var center = x + (barWidth / 2);
                if (day.EventCount > 0 || count <= 14)
                {
                    _graphics.DrawString(
                        day.EventCount.ToString(CultureInfo.InvariantCulture),
                        _smallFont,
                        accent,
                        new XPoint(center, Math.Max(top - 5, bottom - barHeight - 5)),
                        XStringFormats.BaseLineCenter);
                }

                if (index % labelStep == 0 || index == count - 1)
                {
                    _graphics.DrawString(
                        day.Date.ToString("MM-dd", CultureInfo.InvariantCulture),
                        _smallFont,
                        label,
                        new XPoint(center, bottom + 14),
                        XStringFormats.BaseLineCenter);
                }
            }

            _graphics.DrawLine(_axisPen, left, bottom, right, bottom);
            _y = bottom + 30;
        }

        private void DrawDailyTable(IReadOnlyList<DailyActivityStat> stats)
        {
            var columns = new[] { 84d, 60d, 76d, ContentWidth - 220 };
            var headers = new[] { "日期", "事件数", "活跃分钟", "主要应用" };
            DrawTableRow(headers, columns, _smallFont, SectionColor, subtle: true);
            var headerPage = _pageNumber;

            foreach (var day in stats)
            {
                EnsureSpace(22);
                if (_pageNumber != headerPage)
                {
                    DrawTableRow(headers, columns, _smallFont, SectionColor, subtle: true);
                    headerPage = _pageNumber;
                }

                var cells = new[]
                {
                    day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    day.EventCount.ToString(CultureInfo.InvariantCulture),
                    day.ActiveMinutes.ToString(CultureInfo.InvariantCulture),
                    string.Join("、", day.AppCounts.Keys.Take(4))
                };
                DrawTableRow(cells, columns, _smallFont, BodyColor, subtle: false);
            }

            _y += 6;
        }

        private void DrawTableRow(string[] cells, double[] columns, XFont font, XColor color, bool subtle)
        {
            const double rowHeight = 18;
            EnsureSpace(rowHeight + 2);

            var background = new XSolidBrush(SubtleColor);
            var textBrush = new XSolidBrush(color);
            var x = Margin;
            for (var index = 0; index < cells.Length; index++)
            {
                var width = columns[index];
                if (subtle)
                {
                    _graphics.DrawRectangle(background, new XRect(x, _y - 12, width, rowHeight));
                }

                _graphics.DrawRectangle(_gridPen, new XRect(x, _y - 12, width, rowHeight));
                _graphics.DrawString(
                    Fit(cells[index], font, width - 8),
                    font,
                    textBrush,
                    new XPoint(x + 4, _y),
                    XStringFormats.BaseLineLeft);
                x += width;
            }

            _y += rowHeight;
        }

        private void DrawApps(IReadOnlyList<AppDistributionItem> apps)
        {
            var max = apps.Max(item => item.Count);
            var accent = new XSolidBrush(AccentColor);
            var label = new XSolidBrush(BodyColor);
            var muted = new XSolidBrush(MutedColor);
            foreach (var app in apps.Take(10))
            {
                EnsureSpace(16);
                _graphics.DrawString(
                    Fit(app.Name, _smallFont, 200),
                    _smallFont,
                    label,
                    new XPoint(Margin, _y),
                    XStringFormats.BaseLineLeft);
                var barX = Margin + 210;
                var barMax = ContentWidth - 210 - 52;
                var barWidth = Math.Max(1, barMax * (app.Count / (double)Math.Max(1, max)));
                _graphics.DrawRoundedRectangle(accent, new XRect(barX, _y - 9, barWidth, 8), new XSize(2, 2));
                _graphics.DrawString(
                    $"{app.Count} 条",
                    _smallFont,
                    muted,
                    new XPoint(PageWidth - Margin, _y),
                    XStringFormats.BaseLineRight);
                _y += 15;
            }
        }

        private void DrawHighlights(IReadOnlyList<string> highlights)
        {
            var brush = new XSolidBrush(BodyColor);
            foreach (var item in highlights.Take(15))
            {
                EnsureSpace(15);
                _graphics.DrawString(
                    $"· {Fit(item, _smallFont, ContentWidth - 12)}",
                    _smallFont,
                    brush,
                    new XPoint(Margin, _y),
                    XStringFormats.BaseLineLeft);
                _y += 14;
            }
        }

        private IEnumerable<string> Wrap(string text, XFont font, double maxWidth)
        {
            var buffer = new StringBuilder();
            foreach (var ch in text)
            {
                if (ch is '\r' or '\n')
                {
                    if (buffer.Length > 0)
                    {
                        yield return buffer.ToString();
                        buffer.Clear();
                    }

                    continue;
                }

                var candidate = buffer.ToString() + ch;
                if (buffer.Length > 0 && _graphics.MeasureString(candidate, font).Width > maxWidth)
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                    buffer.Append(ch);
                }
                else
                {
                    buffer.Append(ch);
                }
            }

            if (buffer.Length > 0)
            {
                yield return buffer.ToString();
            }
        }

        private string Fit(string text, XFont font, double maxWidth)
        {
            if (_graphics.MeasureString(text, font).Width <= maxWidth)
            {
                return text;
            }

            var candidate = text;
            while (candidate.Length > 1 && _graphics.MeasureString(candidate + "…", font).Width > maxWidth)
            {
                candidate = candidate[..^1];
            }

            return candidate + "…";
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
