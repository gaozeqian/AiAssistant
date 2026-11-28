using System.Globalization;
using AiAssistant.Application.Ports;

namespace AiAssistant.Desktop.ViewModels;

/// <summary>One bar of the weekly range report: date, count label and a pixel width scaled to the busiest day.</summary>
public sealed class DailyBarItem(DailyActivityStat stat, int maxEvents)
{
    public string DateText { get; } = stat.Date.ToString("MM-dd", CultureInfo.InvariantCulture);

    public string CountText { get; } = string.Create(CultureInfo.InvariantCulture, $"{stat.EventCount} 条");

    public double BarWidthPixels { get; } = Math.Max(2, stat.BarWidthPercent(maxEvents) * 2.4);
}
