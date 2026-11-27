using AiAssistant.Core.Configuration;
using AiAssistant.Desktop;
using AiAssistant.Diagnostics.Host.UiE2E;
using AiAssistant.Host;
using AiAssistant.Host.Benchmarks;
using Avalonia;

namespace AiAssistant.Diagnostics.Host;

/// <summary>
/// Developer-only entry point. Benchmarks and UI self-driving code live here, not in the shipped
/// AiAssistant package.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("""
                诊断宿主（不随产品发布）：
                  --benchmark-retrieval [--output <path>]
                  --benchmark-asr [--samples <dir>] [--output <path>]
                  --benchmark-kws [--samples <dir>] [--output <path>]
                  --benchmark-ocr [--samples <dir>] [--output <path>]
                  --ui-e2e [--output <dir>]
                """);
            return 0;
        }

        if (args.Contains("--ui-e2e", StringComparer.OrdinalIgnoreCase))
        {
            return RunUiE2E(args);
        }

        return RunBenchmark(args);
    }

    private static int RunBenchmark(string[] args)
    {
        var runtime = AppRuntime.CreateAsync().GetAwaiter().GetResult();
        try
        {
            if (args.Contains("--benchmark-retrieval", StringComparer.OrdinalIgnoreCase))
            {
                return new RetrievalBenchmarkCommand(runtime.Services.ModelGateway)
                    .RunAsync(OutputPath("retrieval"), CancellationToken.None)
                    .GetAwaiter().GetResult();
            }

            if (args.Contains("--benchmark-asr", StringComparer.OrdinalIgnoreCase))
            {
                return new AsrBenchmarkCommand(runtime.Services.AsrEngine)
                    .RunAsync(SamplesPath("asr"), OutputPath("asr"), CancellationToken.None)
                    .GetAwaiter().GetResult();
            }

            if (args.Contains("--benchmark-kws", StringComparer.OrdinalIgnoreCase))
            {
                return new KwsBenchmarkCommand(runtime.Services.WakeWordDetector)
                    .RunAsync(SamplesPath("kws"), OutputPath("kws"), CancellationToken.None)
                    .GetAwaiter().GetResult();
            }

            if (args.Contains("--benchmark-ocr", StringComparer.OrdinalIgnoreCase))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                runtime.StartMacHelperAsync(cts.Token).GetAwaiter().GetResult();
                if (runtime.Services.OcrRecognizer is not { } ocr)
                {
                    Console.Error.WriteLine("OCR benchmark requires an IImageOcrRecognizer adapter.");
                    return 1;
                }

                return new OcrBenchmarkCommand(ocr)
                    .RunAsync(SamplesPath("ocr"), OutputPath("ocr"), CancellationToken.None)
                    .GetAwaiter().GetResult();
            }

            Console.Error.WriteLine($"Unknown diagnostics command: {args[0]}");
            return 2;
        }
        finally
        {
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static int RunUiE2E(string[] args)
    {
        var runtime = AppRuntime.CreateAsync().GetAwaiter().GetResult();
        try
        {
            Environment.SetEnvironmentVariable(EnvironmentNames.UiE2E, "1");
            Environment.SetEnvironmentVariable(
                EnvironmentNames.UiE2EOutput,
                Environment.GetEnvironmentVariable(EnvironmentNames.UiE2EOutput)
                ?? ArtifactPaths.Resolve(["test-results", "e2e"]));

            // The diagnostics host injects the self-driving runner; the shipped Desktop assembly
            // only knows the delegate type and the immutable launch context.
            var launchContext = new DesktopLaunchContext(
                runtime.CreateShellViewModel,
                runtime.InstanceSocketPath,
                TrayAvailable: false,
                UiE2ERunner.RunAsync);

            return AiAssistant.Host.Program.BuildAvaloniaApp(launchContext)
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static string OutputPath(string kind)
    {
        // The params overload is the one that appends to the artifacts root; a leading string
        // argument would bind to the base-directory overload instead.
        var path = ArtifactPaths.Resolve(["test-results", "benchmarks", $"{kind}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.md"]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static string SamplesPath(string kind) =>
        ArtifactPaths.Resolve(["cache", "models", "golden-sets", kind]);
}
