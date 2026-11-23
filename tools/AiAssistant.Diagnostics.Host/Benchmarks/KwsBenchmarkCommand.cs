using AiAssistant.Application.Ports;
using AiAssistant.Processing.Benchmarks;

namespace AiAssistant.Host.Benchmarks;

/// <summary>
/// Runs the wake-word golden set: `positive/*.wav` clips containing the keyword once at the start,
/// `negative/*.wav` clips of ordinary speech without it. Reports recall, median time-to-detect and
/// false alarms per hour (S7-R1).
/// </summary>
public sealed class KwsBenchmarkCommand(IWakeWordDetector detector)
{
    public async Task<int> RunAsync(string sampleDirectory, string outputPath, CancellationToken ct)
    {
        var warnings = new List<string>();
        var unavailableReason = detector.Capability.IsOperational
            ? null
            : $"detector unavailable: {detector.Capability.Summary}";
        if (unavailableReason is not null)
        {
            Console.Error.WriteLine($"KWS detector unavailable: {detector.Capability.Summary}");
            warnings.Add(unavailableReason);
        }

        var positives = new List<KwsFileResult>();
        var negatives = new List<KwsFileResult>();

        foreach (var path in EnumerateWavs(Path.Combine(sampleDirectory, "positive")))
        {
            ct.ThrowIfCancellationRequested();
            var result = RunOne(path, isPositive: true);
            if (result is null)
            {
                continue;
            }

            positives.Add(result);
            Console.WriteLine(
                $"positive {result.Name}: detected={result.Detected} " +
                $"latency={(result.FirstDetectionOffset?.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) ?? "-")} ms");
        }

        foreach (var path in EnumerateWavs(Path.Combine(sampleDirectory, "negative")))
        {
            ct.ThrowIfCancellationRequested();
            var result = RunOne(path, isPositive: false);
            if (result is null)
            {
                continue;
            }

            negatives.Add(result);
            Console.WriteLine($"negative {result.Name}: falseAlarms={result.DetectionCount} duration={result.Duration.TotalSeconds:F1}s");
        }

        if (positives.Count == 0 && negatives.Count == 0)
        {
            warnings.Add(
                $"no samples found under {sampleDirectory}; expected positive/*.wav and negative/*.wav");
        }

        var summary = KwsBenchmarkEvaluator.Summarize(positives, negatives);
        var report = KwsBenchmarkEvaluator.BuildReport(
            detector.Capability.Summary,
            Path.GetFullPath(sampleDirectory),
            summary,
            warnings,
            TimeProvider.System.GetLocalNow());

        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        await File.WriteAllTextAsync(fullOutputPath, report, ct);

        var recall = double.IsNaN(summary.Recall)
            ? "n/a"
            : (summary.Recall * 100).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "%";
        var falseAlarms = double.IsNaN(summary.FalseAlarmsPerHour)
            ? "n/a"
            : summary.FalseAlarmsPerHour.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "/h";
        Console.WriteLine(
            $"kws-benchmark: positives={positives.Count} recall={recall} falseAlarms={falseAlarms} report={fullOutputPath}");

        return unavailableReason is null && positives.Count > 0 ? 0 : 1;

        KwsFileResult? RunOne(string path, bool isPositive)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            try
            {
                var audio = WavPcm16Reader.ReadAs16kMono(path);
                return KwsBenchmarkEvaluator.RunFile(detector, name, audio);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine($"warning: {name}: {ex.Message}");
                return null;
            }
        }
    }

    private static IEnumerable<string> EnumerateWavs(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.wav").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            : [];
}
