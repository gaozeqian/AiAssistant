using AiAssistant.Core.Domain;

namespace AiAssistant.Application.Ports;

public sealed record DailyActivityStat(
    DateOnly Date,
    int EventCount,
    int ActiveMinutes,
    IReadOnlyDictionary<string, int> AppCounts)
{
    /// <summary>Relative bar width (2-100) used by the weekly chart; zero-event days render as 0.</summary>
    public int BarWidthPercent(int maxEvents) =>
        maxEvents <= 0 || EventCount <= 0 ? 0 : Math.Clamp((int)Math.Round(EventCount * 100.0 / maxEvents), 2, 100);
}
