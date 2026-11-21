using AiAssistant.Application.Ports;
using AiAssistant.Processing.Benchmarks;

namespace AiAssistant.Host.Benchmarks;

/// <summary>
/// Runs the ASR golden set (16 kHz WAV + sibling .txt) through the configured engine and reports
/// character error rate. Works with sherpa-onnx or Whisper depending on AIASSISTANT_ASR_ENGINE;
/// never reads user data (S4-R).
/// </summary>
public sealed class AsrBenchmarkCommand(IAsrEngine asr)
{
    public async Task<int> RunAsync(string sampleDirectory, string outputPath, CancellationToken ct)
    {
        var set = RecognitionSampleSet.Load(sampleDirectory, ".wav");
        foreach (var warning in set.Warnings)
        {
            Console.Error.WriteLine($"warning: {warning}");
        }

        var unavailableReason = asr.Capability.IsOperational
            ? null
            : $"engine unavailable: {asr.Capability.Summary}";
        if (unavailableReason is not null)
        {
            Console.Error.WriteLine($"ASR engine unavailable: {asr.Capability.Summary}");
        }

        string? modelVersion = null;
        var rows = await RecognitionBenchmarkEvaluator.RunAsync(
            set.Samples,
            async (sample, token) =>
            {
                var audio = WavPcm16Reader.ReadAs16kMono(sample.MediaPath);
                var result = await asr.TranscribeAsync(
                    new AudioTranscriptionRequest(audio.Samples, audio.SampleRateHz),
                    token);
                modelVersion ??= result.ModelVersion;
                return result.Text;
            },
            unavailableReason,
            ct);

        var engine = modelVersion is null
            ? asr.Capability.Summary
            : $"{asr.Capability.Summary} (model={modelVersion})";
        var report = RecognitionBenchmarkReport.Build(
            "中文语音识别黄金集（ASR）",
            engine,
            Path.GetFullPath(sampleDirectory),
            rows,
            set.Warnings,
            DateTimeOffset.Now);

        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        await File.WriteAllTextAsync(fullOutputPath, report, ct);
        Console.WriteLine(
            $"asr-benchmark: samples={rows.Count} succeeded={rows.Count(r => r.Succeeded)} " +
            $"avgCer={RecognitionBenchmarkEvaluator.AverageCer(rows)} report={fullOutputPath}");

        return rows.Any(r => r.Succeeded) ? 0 : 1;
    }
}
