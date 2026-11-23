using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Processing.Benchmarks;
using AiAssistant.Testing.Common;

namespace AiAssistant.Processing.Tests;

[Trait("Category", "Unit")]
public sealed class KwsBenchmarkTests
{
    [Fact]
    public void EvaluatorMeasuresRecallLatencyAndFalseAlarms()
    {
        var detector = new MarkerDetector();

        // 1 s of silence with the marker at 300 ms -> detected, latency 300 ms.
        var positiveHit = BuildAudio(seconds: 1, markerFrame: 3);
        var hit = KwsBenchmarkEvaluator.RunFile(detector, "hit", positiveHit);
        Assert.True(hit.Detected);
        Assert.Equal(TimeSpan.FromMilliseconds(300), hit.FirstDetectionOffset);
        Assert.Equal(1, hit.DetectionCount);

        // No marker -> miss.
        var miss = KwsBenchmarkEvaluator.RunFile(detector, "miss", BuildAudio(seconds: 1, markerFrame: null));
        Assert.False(miss.Detected);
        Assert.Null(miss.FirstDetectionOffset);

        // Two markers in 10 s of negative audio -> 2 false alarms, 720/h.
        var negative = KwsBenchmarkEvaluator.RunFile(detector, "negative", BuildAudio(seconds: 10, markerFrame: 5, secondMarkerFrame: 50));
        Assert.Equal(2, negative.DetectionCount);

        var summary = KwsBenchmarkEvaluator.Summarize([hit, miss], [negative]);
        Assert.Equal(0.5, summary.Recall, 3);
        Assert.Equal(10, summary.NegativeDuration.TotalSeconds, 1);
        Assert.Equal(720d, summary.FalseAlarmsPerHour, 1);
        Assert.Equal(TimeSpan.FromMilliseconds(300), summary.MedianLatency);
    }

    [Fact]
    public void MedianLatencyUsesMiddleValue()
    {
        var detector = new MarkerDetector();
        var rows = new[]
        {
            KwsBenchmarkEvaluator.RunFile(detector, "a", BuildAudio(1, markerFrame: 1)),
            KwsBenchmarkEvaluator.RunFile(detector, "b", BuildAudio(1, markerFrame: 5)),
            KwsBenchmarkEvaluator.RunFile(detector, "c", BuildAudio(1, markerFrame: 9))
        };

        var summary = KwsBenchmarkEvaluator.Summarize(rows, []);
        Assert.Equal(TimeSpan.FromMilliseconds(500), summary.MedianLatency);
        Assert.Equal(1d, summary.Recall, 3);
        Assert.True(double.IsNaN(summary.FalseAlarmsPerHour));
    }

    [Fact]
    public void ReportContainsSummaryAndWarnings()
    {
        var detector = new MarkerDetector();
        var positive = KwsBenchmarkEvaluator.RunFile(detector, "hit", BuildAudio(1, markerFrame: 2));
        var summary = KwsBenchmarkEvaluator.Summarize([positive], []);

        var markdown = KwsBenchmarkEvaluator.BuildReport(
            "sherpa-onnx KWS (fake)",
            "artifacts/test-results/golden/kws",
            summary,
            ["negative: not provided"],
            new DateTimeOffset(2026, 11, 21, 18, 30, 0, TimeSpan.FromHours(8)));

        Assert.Contains("唤醒词黄金集（KWS）", markdown, StringComparison.Ordinal);
        Assert.Contains("唤醒率：100.0%", markdown, StringComparison.Ordinal);
        Assert.Contains("唤醒延迟中位数：200 ms", markdown, StringComparison.Ordinal);
        Assert.Contains("negative: not provided", markdown, StringComparison.Ordinal);
        Assert.Contains("| hit | 是 | 200 ms | 1 |", markdown, StringComparison.Ordinal);
    }

    private static PcmAudio BuildAudio(int seconds, int? markerFrame, int? secondMarkerFrame = null)
    {
        var samples = new float[seconds * WavPcm16Reader.TargetSampleRateHz];
        if (markerFrame is { } first)
        {
            samples[first * 1600] = MarkerDetector.Marker;
        }

        if (secondMarkerFrame is { } second)
        {
            samples[second * 1600] = MarkerDetector.Marker;
        }

        return new PcmAudio(samples, WavPcm16Reader.TargetSampleRateHz);
    }

    private sealed class MarkerDetector : IWakeWordDetector
    {
        public const float Marker = 1f;

        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.WakeWord, "fake marker KWS");

        public IReadOnlyList<WakeWordDetection> Accept(AudioFrame frame) =>
            frame.Samples.Length > 0 && Math.Abs(frame.Samples.Span[0] - Marker) < 0.001f
                ? [new WakeWordDetection("你好时光", frame.TimestampUtc)]
                : [];

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }
}
