using AiAssistant.Application.Ports;

namespace AiAssistant.Processing.Benchmarks;

public sealed record KwsFileResult(
    string Name,
    TimeSpan Duration,
    bool Detected,
    TimeSpan? FirstDetectionOffset,
    int DetectionCount);

public sealed record KwsBenchmarkSummary(
    IReadOnlyList<KwsFileResult> Positives,
    IReadOnlyList<KwsFileResult> Negatives)
{
    public double Recall => Positives.Count == 0
        ? double.NaN
        : Positives.Count(p => p.Detected) / (double)Positives.Count;

    public TimeSpan NegativeDuration =>
        TimeSpan.FromTicks(Negatives.Sum(n => n.Duration.Ticks));

    /// <summary>False alarms normalized to an hourly rate; NaN when there is no negative audio.</summary>
    public double FalseAlarmsPerHour
    {
        get
        {
            var hours = NegativeDuration.TotalHours;
            if (hours <= 0)
            {
                return double.NaN;
            }

            return Negatives.Sum(n => n.DetectionCount) / hours;
        }
    }

    public TimeSpan? MedianLatency
    {
        get
        {
            var latencies = Positives
                .Where(p => p.FirstDetectionOffset is not null)
                .Select(p => p.FirstDetectionOffset!.Value.TotalMilliseconds)
                .OrderBy(ms => ms)
                .ToArray();
            if (latencies.Length == 0)
            {
                return null;
            }

            var middle = latencies.Length / 2;
            var medianMs = latencies.Length % 2 == 1
                ? latencies[middle]
                : (latencies[middle - 1] + latencies[middle]) / 2;
            return TimeSpan.FromMilliseconds(medianMs);
        }
    }
}

/// <summary>
/// Streaming evaluation of a wake-word detector: positives measure recall and time-to-detect,
/// negatives measure false alarms per hour (S7-R1).
/// </summary>
public static class KwsBenchmarkEvaluator
{
    public const int FrameMilliseconds = 100;

    public static KwsFileResult RunFile(IWakeWordDetector detector, string name, PcmAudio audio)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(audio);

        detector.Reset();
        var samplesPerFrame = Math.Max(1, audio.SampleRateHz * FrameMilliseconds / 1000);
        var frameCount = (audio.Samples.Length + samplesPerFrame - 1) / samplesPerFrame;
        var startedAt = DateTimeOffset.UnixEpoch;

        TimeSpan? firstDetection = null;
        var detections = 0;
        for (var frame = 0; frame < frameCount; frame++)
        {
            var offset = frame * samplesPerFrame;
            var length = Math.Min(samplesPerFrame, audio.Samples.Length - offset);
            var slice = audio.Samples.Slice(offset, length);
            var timestamp = startedAt + TimeSpan.FromMilliseconds(frame * FrameMilliseconds);
            var hits = detector.Accept(new AudioFrame(timestamp, audio.SampleRateHz, 1, slice));
            if (hits.Count == 0)
            {
                continue;
            }

            detections += hits.Count;
            firstDetection ??= timestamp - startedAt;
        }

        var duration = TimeSpan.FromSeconds(audio.Samples.Length / (double)audio.SampleRateHz);
        return new KwsFileResult(name, duration, detections > 0, firstDetection, detections);
    }

    public static KwsBenchmarkSummary Summarize(
        IReadOnlyList<KwsFileResult> positives,
        IReadOnlyList<KwsFileResult> negatives) => new(positives, negatives);

    public static string BuildReport(
        string engine,
        string sampleDirectory,
        KwsBenchmarkSummary summary,
        IReadOnlyList<string> warnings,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var recallText = double.IsNaN(summary.Recall)
            ? "n/a"
            : (summary.Recall * 100).ToString("F1", culture) + "%";
        var falseAlarmText = double.IsNaN(summary.FalseAlarmsPerHour)
            ? "n/a"
            : summary.FalseAlarmsPerHour.ToString("F2", culture) + " 次/小时";
        var latencyText = summary.MedianLatency is { } latency
            ? latency.TotalMilliseconds.ToString("F0", culture) + " ms"
            : "n/a";

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("# 唤醒词黄金集（KWS）");
        builder.AppendLine();
        builder.AppendLine(culture, $"- 生成时间：{generatedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine(culture, $"- 引擎：{engine}");
        builder.AppendLine(culture, $"- 样本目录：{sampleDirectory}");
        builder.AppendLine(culture, $"- 正样本：{summary.Positives.Count}（命中 {summary.Positives.Count(p => p.Detected)}）");
        builder.AppendLine(culture, $"- 负样本：{summary.Negatives.Count}（总时长 {summary.NegativeDuration.TotalMinutes:F1} 分钟）");
        builder.AppendLine(culture, $"- 唤醒率：{recallText}");
        builder.AppendLine(culture, $"- 误唤醒率：{falseAlarmText}");
        builder.AppendLine(culture, $"- 唤醒延迟中位数：{latencyText}");
        builder.AppendLine();

        if (summary.Positives.Count > 0)
        {
            builder.AppendLine("## 正样本");
            builder.AppendLine();
            builder.AppendLine("| 样本 | 命中 | 首次偏移 | 命中次数 | 时长 |");
            builder.AppendLine("|---|---|---|---|---|");
            foreach (var row in summary.Positives)
            {
                builder.Append("| ").Append(row.Name);
                builder.Append(" | ").Append(row.Detected ? "是" : "否");
                builder.Append(" | ").Append(row.FirstDetectionOffset is { } offset
                    ? offset.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " ms"
                    : "-");
                builder.Append(" | ").Append(row.DetectionCount);
                builder.Append(" | ").Append(row.Duration.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(" s");
                builder.AppendLine(" |");
            }
        }

        if (summary.Negatives.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 负样本");
            builder.AppendLine();
            builder.AppendLine("| 样本 | 误唤醒次数 | 时长 |");
            builder.AppendLine("|---|---|---|");
            foreach (var row in summary.Negatives)
            {
                builder.Append("| ").Append(row.Name);
                builder.Append(" | ").Append(row.DetectionCount);
                builder.Append(" | ").Append(row.Duration.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(" s");
                builder.AppendLine(" |");
            }
        }

        if (warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 警告");
            builder.AppendLine();
            foreach (var warning in warnings)
            {
                builder.Append("- ").AppendLine(warning);
            }
        }

        return builder.ToString();
    }
}
