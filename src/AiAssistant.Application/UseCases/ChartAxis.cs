using System.Globalization;

namespace AiAssistant.Application.UseCases;

/// <summary>
/// Y-axis tick selection shared by the SVG, PNG/PDF (Skia) and PDF (PDFsharp) renderers.
/// A fixed four-interval axis produced duplicate labels for small ranges (3 events rendered as
/// 0, 1, 2, 2, 3), so the interval count is capped by the maximum value instead.
/// </summary>
public static class ChartAxis
{
    public const int MaxIntervals = 4;

    /// <summary>Number of intervals, never more than the maximum value so labels stay distinct.</summary>
    public static int Intervals(int maxEvents) => Math.Clamp(maxEvents, 1, MaxIntervals);

    /// <summary>Tick value for <paramref name="step"/> of <paramref name="intervals"/>, rounded away from zero.</summary>
    public static int Value(int maxEvents, int step, int intervals) =>
        (int)Math.Round(maxEvents * (step / (double)intervals), MidpointRounding.AwayFromZero);

    /// <summary>Formatted tick label, invariant culture so exports are stable across machines.</summary>
    public static string Label(int maxEvents, int step, int intervals) =>
        Value(maxEvents, step, intervals).ToString(CultureInfo.InvariantCulture);
}
