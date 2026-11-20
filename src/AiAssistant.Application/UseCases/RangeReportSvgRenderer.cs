using System.Globalization;
using System.Text;
using AiAssistant.Application.Ports;

namespace AiAssistant.Application.UseCases;

/// <summary>
/// Renders a weekly/range report as a standalone SVG (no external imaging dependency).
/// The output is deterministic, self-contained and safe to embed in documents or browsers.
/// </summary>
public static class RangeReportSvgRenderer
{
    private const int Width = 960;
    private const int Height = 420;
    private const int ChartLeft = 60;
    private const int ChartRight = 900;
    private const int ChartTop = 110;
    private const int ChartBottom = 330;

    public static string Render(RangeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Width}\" height=\"{Height}\" viewBox=\"0 0 {Width} {Height}\" role=\"img\" aria-label=\"区间活动报告\">");
        builder.AppendLine("  <rect width=\"100%\" height=\"100%\" fill=\"#FFFFFF\"/>");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"  <text x=\"48\" y=\"48\" font-family=\"Segoe UI, PingFang SC, sans-serif\" font-size=\"24\" font-weight=\"600\" fill=\"#111C36\">{Escape(Title(report))}</text>");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"  <text x=\"48\" y=\"76\" font-family=\"Segoe UI, PingFang SC, sans-serif\" font-size=\"13\" fill=\"#64748B\">{Escape(Subtitle(report))}</text>");

        if (report.DailyStats.Count == 0 || report.TotalEvents == 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"  <text x=\"{Width / 2}\" y=\"220\" text-anchor=\"middle\" font-family=\"Segoe UI, PingFang SC, sans-serif\" font-size=\"16\" fill=\"#64748B\">该区间没有可用的本地活动记录</text>");
        }
        else
        {
            AppendChart(builder, report);
        }

        builder.AppendLine(CultureInfo.InvariantCulture,
            $"  <text x=\"48\" y=\"400\" font-family=\"Segoe UI, PingFang SC, sans-serif\" font-size=\"11\" fill=\"#94A3B8\">由 AiAssistant 本地生成 · 数据已脱敏</text>");
        builder.AppendLine("</svg>");
        return builder.ToString();
    }

    private static void AppendChart(StringBuilder builder, RangeReport report)
    {
        var maxEvents = Math.Max(1, report.MaxDailyEvents);
        for (var step = 0; step <= 4; step++)
        {
            var ratio = step / 4.0;
            var y = ChartBottom - ((ChartBottom - ChartTop) * ratio);
            var value = (int)Math.Round(maxEvents * ratio);
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"  <line x1=\"{ChartLeft}\" y1=\"{y:F1}\" x2=\"{ChartRight}\" y2=\"{y:F1}\" stroke=\"#E2E8F0\" stroke-width=\"1\"/>");
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"  <text x=\"{ChartLeft - 10}\" y=\"{y + 4:F1}\" text-anchor=\"end\" font-family=\"Segoe UI, PingFang SC, sans-serif\" font-size=\"10\" fill=\"#94A3B8\">{value}</text>");
        }

        var count = report.DailyStats.Count;
        var slotWidth = (double)(ChartRight - ChartLeft) / count;
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
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"  <rect x=\"{x:F1}\" y=\"{y:F1}\" width=\"{barWidth:F1}\" height=\"{barHeight:F1}\" rx=\"4\" fill=\"#3157D5\"/>");
            }

            builder.AppendLine(CultureInfo.InvariantCulture,
                $"  <text x=\"{x + (barWidth / 2):F1}\" y=\"{Math.Max(ChartTop - 6, y - 8):F1}\" text-anchor=\"middle\" font-family=\"Segoe UI, PingFang SC, sans-serif\" font-size=\"11\" font-weight=\"600\" fill=\"#3157D5\">{day.EventCount}</text>");
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"  <text x=\"{x + (barWidth / 2):F1}\" y=\"{ChartBottom + 22}\" text-anchor=\"middle\" font-family=\"Segoe UI, PingFang SC, sans-serif\" font-size=\"11\" fill=\"#475569\">{day.Date.ToString("MM-dd", CultureInfo.InvariantCulture)}</text>");
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"  <text x=\"{x + (barWidth / 2):F1}\" y=\"{ChartBottom + 40}\" text-anchor=\"middle\" font-family=\"Segoe UI, PingFang SC, sans-serif\" font-size=\"10\" fill=\"#94A3B8\">{day.ActiveMinutes} 分钟</text>");
        }

        builder.AppendLine(CultureInfo.InvariantCulture,
            $"  <line x1=\"{ChartLeft}\" y1=\"{ChartBottom}\" x2=\"{ChartRight}\" y2=\"{ChartBottom}\" stroke=\"#94A3B8\" stroke-width=\"1\"/>");
    }

    private static string Title(RangeReport report) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{report.FromDate:yyyy年MM月dd日} – {report.ToDate:yyyy年MM月dd日} 活动报告");

    private static string Subtitle(RangeReport report) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"共 {report.TotalEvents} 条事件 · 活跃约 {report.TotalActiveMinutes} 分钟 · 模型 {report.ModelId}{(report.Degraded ? "（统计模式）" : string.Empty)}");

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);
}
