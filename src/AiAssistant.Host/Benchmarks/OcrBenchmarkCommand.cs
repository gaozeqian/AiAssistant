using AiAssistant.Application.Imaging;
using AiAssistant.Application.Ports;
using AiAssistant.Platform.Linux;
using AiAssistant.Processing.Benchmarks;

namespace AiAssistant.Host.Benchmarks;

/// <summary>
/// Runs the OCR golden set (PNG screenshots + sibling .txt) through the configured image OCR
/// adapter and reports character error rate. Only platforms that expose IImageOcrRecognizer
/// (currently Linux RapidOCR) can run this; elsewhere the command exits with code 1 (S4-R).
/// </summary>
public sealed class OcrBenchmarkCommand(IImageOcrRecognizer? ocr)
{
    private static readonly string[] MediaExtensions = [".png", ".jpg", ".jpeg"];

    public async Task<int> RunAsync(string sampleDirectory, string outputPath, CancellationToken ct)
    {
        if (ocr is null)
        {
            Console.Error.WriteLine(
                "OCR benchmark requires a platform image OCR adapter (macOS Vision or Linux RapidOCR). " +
                "On Linux set AIASSISTANT_RAPIDOCR_MODEL_DIR; on macOS the Swift helper must be available.");
            return 1;
        }

        var set = RecognitionSampleSet.Load(sampleDirectory, MediaExtensions);
        foreach (var warning in set.Warnings)
        {
            Console.Error.WriteLine($"warning: {warning}");
        }

        var unavailableReason = ocr.Capability.IsOperational
            ? null
            : $"adapter unavailable: {ocr.Capability.Summary}";
        if (unavailableReason is not null)
        {
            Console.Error.WriteLine($"OCR adapter unavailable: {ocr.Capability.Summary}");
        }

        var rows = await RecognitionBenchmarkEvaluator.RunAsync(
            set.Samples,
            async (sample, token) =>
            {
                var bytes = await File.ReadAllBytesAsync(sample.MediaPath, token);
                if (!PngCodec.TryDecodeToBgra32(bytes, out var bgra, out var width, out var height, out _) ||
                    bgra is null)
                {
                    throw new InvalidDataException(
                        "unsupported image format; the managed decoder accepts PNG only");
                }

                return await ocr.RecognizeAsync(new ImageFrameBuffer(bgra, width, height), token) ?? string.Empty;
            },
            unavailableReason,
            ct);

        var report = RecognitionBenchmarkReport.Build(
            "图像 OCR 黄金集",
            ocr.Capability.Summary,
            Path.GetFullPath(sampleDirectory),
            rows,
            set.Warnings,
            DateTimeOffset.Now);

        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        await File.WriteAllTextAsync(fullOutputPath, report, ct);
        Console.WriteLine(
            $"ocr-benchmark: samples={rows.Count} succeeded={rows.Count(r => r.Succeeded)} " +
            $"avgCer={RecognitionBenchmarkEvaluator.AverageCer(rows)} report={fullOutputPath}");

        return rows.Any(r => r.Succeeded) ? 0 : 1;
    }
}
