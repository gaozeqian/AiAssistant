using AiAssistant.Core.Configuration;
using AiAssistant.Host;
using AiAssistant.Host.Benchmarks;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiAssistant.Diagnostics.Host;

/// <summary>
/// Developer-only entry point. Benchmarks and UI self-driving code are compiled here, not into
/// the shipped AiAssistant.Host package.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("""
                诊断宿主（不随产品发布）：
                  --benchmark-retrieval [--output <path>]
                  --benchmark-asr [--samples <dir>] [--output <path>]
                  --benchmark-kws [--samples <dir>] [--output <path>]
                  --benchmark-ocr [--samples <dir>] [--output <path>]
                """);
            return 0;
        }

        var runtime = await AppRuntime.CreateAsync();
        try
        {
            if (args.Contains("--benchmark-retrieval", StringComparer.OrdinalIgnoreCase))
            {
                return await new RetrievalBenchmarkCommand(runtime.ModelGateway)
                    .RunAsync(OutputPath("retrieval"), CancellationToken.None);
            }

            if (args.Contains("--benchmark-asr", StringComparer.OrdinalIgnoreCase))
            {
                return await new AsrBenchmarkCommand(runtime.AsrEngine)
                    .RunAsync(SamplesPath("asr"), OutputPath("asr"), CancellationToken.None);
            }

            if (args.Contains("--benchmark-kws", StringComparer.OrdinalIgnoreCase))
            {
                return await new KwsBenchmarkCommand(runtime.WakeWordDetector)
                    .RunAsync(SamplesPath("kws"), OutputPath("kws"), CancellationToken.None);
            }

            if (args.Contains("--benchmark-ocr", StringComparer.OrdinalIgnoreCase))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await runtime.StartMacHelperAsync(cts.Token);
                if (runtime.OcrRecognizer is not { } ocr)
                {
                    Console.Error.WriteLine("OCR benchmark requires an IImageOcrRecognizer adapter.");
                    return 1;
                }

                return await new OcrBenchmarkCommand(ocr)
                    .RunAsync(SamplesPath("ocr"), OutputPath("ocr"), CancellationToken.None);
            }

            Console.Error.WriteLine($"Unknown diagnostics command: {args[0]}");
            return 2;
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    private static string OutputPath(string kind)
    {
        var path = ArtifactPaths.Resolve("evidence", "benchmarks", $"{kind}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static string SamplesPath(string kind) =>
        ArtifactPaths.Resolve("models", "golden-sets", kind);
}
