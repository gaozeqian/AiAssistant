using System.Globalization;
using System.Reflection;
using System.Text;
using AiAssistant.Application.UseCases;
using AiAssistant.Host.Benchmarks;
using AiAssistant.Platform.Linux.Desktop;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace AiAssistant.Host;

public static class Program
{
    /// <summary>
    /// Set when the async prologue reached the point where the desktop shell should start.
    /// AppKit and Avalonia both require the process main thread, but continuations after an
    /// awaited call resume on pool threads, so the UI is started back in <see cref="Main"/> (S3-R1).
    /// </summary>
    private sealed class DesktopShellHandoff
    {
        public bool Requested { get; set; }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // No console attached (service/tray mode); keep default encoding.
        }

        if (args.Contains("--smoke-check", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("AiAssistant.Host smoke-check OK");
            return 0;
        }

        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        if (args.Contains("--version", StringComparer.OrdinalIgnoreCase))
        {
            var version = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(Program).Assembly.GetName().Version?.ToString()
                ?? "unknown";
            Console.WriteLine($"AiAssistant {version}");
            return 0;
        }

        try
        {
            // Blocking is intentional: it keeps the main thread free to own the AppKit/Avalonia loop.
            var runtime = AppRuntime.CreateAsync().GetAwaiter().GetResult();
            try
            {
                if (args.Contains("--ui-e2e", StringComparer.OrdinalIgnoreCase))
                {
                    // Give the self-driven UI flow a realistic day of local activity to search/report on.
                    runtime.DatabaseMaintenance.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
                    var seeded = runtime.SeedUiE2EEventsAsync().GetAwaiter().GetResult();
                    if (seeded > 0)
                    {
                        Console.WriteLine($"ui-e2e: seeded {seeded} synthetic events");
                    }
                }

                var handoff = new DesktopShellHandoff();
                var exitCode = RunAsync(runtime, args, handoff).GetAwaiter().GetResult();
                if (!handoff.Requested)
                {
                    return exitCode;
                }

                // UI E2E 钩子：`--show-window` 让 tray-only 应用直接显示主窗口并隔离数据目录，
                // 供外部 AX 驱动测试；`--ui-e2e` 额外启用内置真实 UI 自驱动流程（真实窗口 + 真实渲染）。
                if (args.Contains("--show-window", StringComparer.OrdinalIgnoreCase))
                {
                    Environment.SetEnvironmentVariable("AIASSISTANT_UI_E2E_SHOW", "1");
                }

                if (args.Contains("--ui-e2e", StringComparer.OrdinalIgnoreCase))
                {
                    Environment.SetEnvironmentVariable("AIASSISTANT_UI_E2E_SHOW", "1");
                    Environment.SetEnvironmentVariable("AIASSISTANT_UI_E2E", "1");
                }

                if (TryGetOption(args, "--data-directory", out var dataDirectory))
                {
                    Environment.SetEnvironmentVariable("AIASSISTANT_DATA_DIRECTORY", dataDirectory);
                }

                Desktop.ShellApp.TrayAvailable = runtime.IsSystemTraySupported;
                Desktop.ShellApp.ViewModelFactory = runtime.CreateShellViewModel;
                return OperatingSystem.IsLinux() && runtime.LinuxBackendState is { } linuxBackend
                    ? StartLinuxDesktop(linuxBackend, args)
                    : BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                // Platform adapters can block on teardown (native event loop, audio device, IPC).
                // Bound it so closing the window always exits instead of leaving an orphan process.
                try
                {
                    runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                }
                catch (TimeoutException)
                {
                    // Bounded shutdown: the OS reclaims remaining resources when the process exits.
                }
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"AiAssistant usage error: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AiAssistant startup failed ({ex.GetType().Name}): {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> RunAsync(AppRuntime runtime, string[] args, DesktopShellHandoff handoff)
    {
        try
        {
            if (args.Contains("--mcp-token", StringComparer.OrdinalIgnoreCase))
            {
                // Explicit local action: prints the bearer token so the user can paste it into an MCP client.
                Console.WriteLine(await runtime.McpControl.RevealTokenAsync(CancellationToken.None));
                return 0;
            }

            if (args.Contains("--mcp-serve", StringComparer.OrdinalIgnoreCase))
            {
                await runtime.StartMcpOnlyAsync(CancellationToken.None);
                var mcpStatus = runtime.McpControl.GetSnapshot();
                if (!mcpStatus.Running || mcpStatus.Endpoint is null)
                {
                    Console.Error.WriteLine("Local MCP is disabled. Set AIASSISTANT_MCP_ENABLED=true to enable it.");
                    return 3;
                }

                Console.WriteLine($"Local read-only MCP listening on {mcpStatus.Endpoint}");
                using var shutdown = new CancellationTokenSource();
                Console.CancelKeyPress += (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    shutdown.Cancel();
                };
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    // Normal Ctrl+C shutdown
                }

                return 0;
            }

            if (args.Contains("--doctor", StringComparer.OrdinalIgnoreCase))
            {
                await runtime.DatabaseMaintenance.InitializeAsync(CancellationToken.None);
                var health = await runtime.DatabaseMaintenance.CheckHealthAsync(CancellationToken.None);
                Console.WriteLine(
                    $"storage: healthy={health.IsHealthy} schema=v{health.SchemaVersion.Number} " +
                    $"events={health.EventCount} media={health.EncryptedBlobCount} integrity={health.IntegrityMessage}");

                try
                {
                    using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var capabilities = await runtime.ModelCapabilityProber.ProbeAllAsync(
                        forceRefresh: false,
                        probeCts.Token);
                    if (capabilities.Count == 0)
                    {
                        Console.WriteLine("models: none configured");
                    }

                    foreach (var capability in capabilities)
                    {
                        Console.WriteLine(
                            $"model {capability.Tier}: chat={capability.ChatAvailable} " +
                            $"embed={capability.EmbeddingsAvailable} json={capability.StructuredOutputAvailable} " +
                            $"tools={capability.ToolCallingAvailable} | {capability.Diagnostic}");
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("models: probe timed out after 10s");
                }

                if (runtime.MacHelperSupervisor is { } macHelper)
                {
                    try
                    {
                        using var helperCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await macHelper.StartAsync(helperCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Helper start timed out; the status line below still reports the supervisor state.
                    }

                    Console.WriteLine(
                        $"macosHelper: status={macHelper.Status} restarts={macHelper.RestartCount} " +
                        $"screen={macHelper.Permissions.ScreenRecording} accessibility={macHelper.Permissions.Accessibility} " +
                        $"microphone={macHelper.Permissions.Microphone}");
                }

                if (runtime.Capabilities is { } capabilityProvider)
                {
                    foreach (var capability in capabilityProvider.GetCapabilities())
                    {
                        Console.WriteLine($"capability {capability.Kind}: {capability.Status} | {capability.Summary}");
                    }
                }

                var mcp = runtime.McpControl.GetSnapshot();
                Console.WriteLine(
                    $"mcp: enabled={mcp.Enabled} running={mcp.Running} endpoint={mcp.Endpoint ?? "-"}");
                Console.WriteLine(
                    $"pdfFont: {AiAssistant.Export.PdfSharpReportRenderer.ResolvedFontPath ?? "none (vector fallback)"}");
                return health.IsHealthy ? 0 : 1;
            }

            if (TryGetOption(args, "--import-model-secret", out var secretName))
            {
                Console.Error.Write($"Secret value for '{secretName}': ");
                var secretValue = ReadSecretFromConsole();
                if (string.IsNullOrWhiteSpace(secretValue))
                {
                    Console.Error.WriteLine("Secret value is empty; nothing was stored.");
                    return 3;
                }

                await runtime.ModelSecretAdmin.SetAsync(secretName, secretValue, CancellationToken.None);
                Console.WriteLine($"Secret '{secretName}' stored encrypted.");
                return 0;
            }

            if (args.Contains("--list-model-secrets", StringComparer.OrdinalIgnoreCase))
            {
                var names = await runtime.ModelSecretAdmin.ListNamesAsync(CancellationToken.None);
                if (names.Count == 0)
                {
                    Console.WriteLine("No model secrets stored.");
                }

                foreach (var name in names)
                {
                    Console.WriteLine(name);
                }

                return 0;
            }

            if (TryGetOption(args, "--delete-model-secret", out var deleteSecretName))
            {
                if (!args.Contains("--confirm-delete-secret", StringComparer.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("Refusing to delete a model secret without --confirm-delete-secret.");
                    return 3;
                }

                var deleted = await runtime.ModelSecretAdmin.DeleteAsync(deleteSecretName, CancellationToken.None);
                Console.WriteLine(deleted
                    ? $"Secret '{deleteSecretName}' deleted."
                    : $"Secret '{deleteSecretName}' not found.");
                return deleted ? 0 : 1;
            }

            if (args.Contains("--probe-model", StringComparer.OrdinalIgnoreCase))
            {
                var capabilities = await runtime.ModelCapabilityProber.ProbeAllAsync(
                    forceRefresh: true,
                    CancellationToken.None);
                if (capabilities.Count == 0)
                {
                    Console.WriteLine("No model endpoints configured.");
                    return 0;
                }

                foreach (var capability in capabilities)
                {
                    Console.WriteLine(
                        $"{capability.Tier}: chat={capability.ChatAvailable} stream={capability.StreamingAvailable} " +
                        $"embed={capability.EmbeddingsAvailable} json={capability.StructuredOutputAvailable} " +
                        $"tools={capability.ToolCallingAvailable} model={capability.ChatModel} | {capability.Diagnostic}");
                }

                return 0;
            }

            if (args.Contains("--benchmark-model", StringComparer.OrdinalIgnoreCase))
            {
                var report = await runtime.ModelBenchmark.RunAsync(CancellationToken.None);
                var outputPath = ResolveBenchmarkOutput(args, "model");
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await File.WriteAllTextAsync(outputPath, report.ToMarkdown(), CancellationToken.None);
                Console.WriteLine(
                    $"Model benchmark: model={report.ChatModel} success={report.SuccessRate:P0} json={report.JsonComplianceRate:P0} avg={report.AverageLatencyMilliseconds}ms");
                Console.WriteLine($"report={outputPath}");
                return report.SuccessRate > 0 ? 0 : 1;
            }

            if (args.Contains("--benchmark-retrieval", StringComparer.OrdinalIgnoreCase))
            {
                var outputPath = ResolveBenchmarkOutput(args, "retrieval");
                return await new RetrievalBenchmarkCommand(runtime.ModelGateway)
                    .RunAsync(outputPath, CancellationToken.None);
            }

            if (args.Contains("--benchmark-asr", StringComparer.OrdinalIgnoreCase))
            {
                var samples = ResolveSamplesDirectory(args, "asr");
                var outputPath = ResolveBenchmarkOutput(args, "asr");
                return await new AsrBenchmarkCommand(runtime.AsrEngine)
                    .RunAsync(samples, outputPath, CancellationToken.None);
            }

            if (args.Contains("--benchmark-kws", StringComparer.OrdinalIgnoreCase))
            {
                var samples = ResolveSamplesDirectory(args, "kws");
                var outputPath = ResolveBenchmarkOutput(args, "kws");
                return await new KwsBenchmarkCommand(runtime.WakeWordDetector)
                    .RunAsync(samples, outputPath, CancellationToken.None);
            }

            if (args.Contains("--benchmark-ocr", StringComparer.OrdinalIgnoreCase))
            {
                using var helperCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await runtime.StartMacHelperAsync(helperCts.Token);

                var samples = ResolveSamplesDirectory(args, "ocr");
                var outputPath = ResolveBenchmarkOutput(args, "ocr");
                return await new OcrBenchmarkCommand(runtime.OcrRecognizer)
                    .RunAsync(samples, outputPath, CancellationToken.None);
            }

            var isMaintenanceCommand = args.Any(argument =>
                string.Equals(argument, "--backup", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "--restore", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "--purge-data", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "--rebuild-vectors", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "--report-range", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "--ask", StringComparison.OrdinalIgnoreCase));
            if (isMaintenanceCommand)
            {
                // Maintenance commands bypass StartAsync, so the encrypted schema must be initialized explicitly.
                await runtime.DatabaseMaintenance.InitializeAsync(CancellationToken.None);
            }

            if (args.Contains("--report-range", StringComparer.OrdinalIgnoreCase))
            {
                var today = DateOnly.FromDateTime(DateTime.Today);
                var from = ParseDateOption(args, "--from", today.AddDays(-6));
                var to = ParseDateOption(args, "--to", today);
                var outputDirectory = ResolveOutputDirectory(args, "reports");
                var report = await runtime.RangeReport.GenerateAsync(from, to, CancellationToken.None);
                var export = await runtime.RangeReport.ExportAsync(report, outputDirectory, CancellationToken.None);
                Console.WriteLine(
                    $"Range report: from={from:yyyy-MM-dd} to={to:yyyy-MM-dd} events={report.TotalEvents} " +
                    $"model={report.ModelId} degraded={report.Degraded}");
                Console.WriteLine($"markdown={export.MarkdownPath}");
                Console.WriteLine($"svg={export.SvgPath}");
                Console.WriteLine($"stats={export.StatsPath}");
                if (export.PngPath is not null)
                {
                    Console.WriteLine($"png={export.PngPath}");
                }

                if (export.PdfPath is not null)
                {
                    Console.WriteLine($"pdf={export.PdfPath}");
                }

                return 0;
            }

            if (TryGetOption(args, "--ask", out var question))
            {
                var days = ParseIntOption(args, "--days", 7, 1, 366);
                var answer = await runtime.EvidenceSearch.AskRecentAsync(question, days, CancellationToken.None);
                Console.WriteLine(answer.Answer);
                Console.WriteLine(
                    $"citations={string.Join(",", answer.CitedEvidenceIds)} model={answer.ModelId} degraded={answer.Degraded}");
                foreach (var evidence in answer.Evidence)
                {
                    Console.WriteLine(
                        $"[{evidence.EvidenceId}] {evidence.LocalDate:yyyy-MM-dd} {evidence.AppName} | {evidence.Title}");
                }

                if (TryGetOption(args, "--output", out var exportDirectory))
                {
                    var path = await runtime.EvidenceSearch.ExportAnswerAsync(
                        answer,
                        Path.GetFullPath(exportDirectory),
                        CancellationToken.None);
                    Console.WriteLine($"export={path}");
                }

                return answer.HasEvidence ? 0 : 1;
            }

            if (args.Contains("--rebuild-vectors", StringComparer.OrdinalIgnoreCase))
            {
                var indexed = await runtime.EmbeddingIndexer.RebuildAllAsync(CancellationToken.None);
                Console.WriteLine($"Vector index rebuilt for {indexed} events using model {runtime.ModelGateway.EmbeddingModelId}.");
                return 0;
            }

            if (TryGetOption(args, "--backup", out var backupPath))
            {
                await runtime.DatabaseMaintenance.BackupEncryptedAsync(Path.GetFullPath(backupPath), CancellationToken.None);
                Console.WriteLine($"AiAssistant encrypted backup written to {Path.GetFullPath(backupPath)}");
                return 0;
            }

            if (TryGetOption(args, "--restore", out var restorePath))
            {
                if (!args.Contains("--confirm-restore", StringComparer.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("Refusing to restore without --confirm-restore; existing data would be replaced.");
                    return 3;
                }

                await runtime.DatabaseMaintenance.RestoreEncryptedAsync(Path.GetFullPath(restorePath), CancellationToken.None);
                Console.WriteLine($"AiAssistant encrypted backup restored from {Path.GetFullPath(restorePath)}");
                return 0;
            }

            if (args.Contains("--purge-data", StringComparer.OrdinalIgnoreCase))
            {
                if (!args.Contains("--confirm-purge", StringComparer.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("Refusing to delete all local data without --confirm-purge.");
                    return 3;
                }

                var deleted = await runtime.DataDeletion.PurgeAllAsync(CancellationToken.None);
                Console.WriteLine(
                    $"AiAssistant local data purged: events={deleted.EventsDeleted} media={deleted.MediaFilesDeleted} reports={deleted.ReportsDeleted} jobs={deleted.JobsDeleted} voice={deleted.VoiceCommandsDeleted}");
                return 0;
            }

            await runtime.StartAsync();
            if (TryGetOption(args, "--capture-smoke", out var captureSecondsText))
            {
                // Real-machine acceptance helper (S3-R1): runs the capture pipeline headless for N seconds
                // and reports what actually reached the encrypted store, without starting the desktop shell.
                var captureSeconds = int.TryParse(captureSecondsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSeconds)
                    ? Math.Clamp(parsedSeconds, 5, 300)
                    : 20;
                await Task.Delay(TimeSpan.FromSeconds(captureSeconds), CancellationToken.None);
                var captureHealth = await runtime.DatabaseMaintenance.CheckHealthAsync(CancellationToken.None);
                Console.WriteLine(
                    $"capture-smoke: seconds={captureSeconds} events={captureHealth.EventCount} media={captureHealth.EncryptedBlobCount} healthy={captureHealth.IsHealthy}");
                return captureHealth.EventCount > 0 ? 0 : 1;
            }

            if (args.Contains("--startup-check", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"AiAssistant.Host startup-check OK; recovered jobs: {runtime.RecoveredJobCount}");
                return 0;
            }

            // AppKit/Avalonia require the process main thread; Main starts the shell there.
            handoff.Requested = true;
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"AiAssistant usage error: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AiAssistant startup failed ({ex.GetType().Name}): {ex.Message}");
            return 2;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(null);

    private static bool TryGetOption(string[] args, string name, out string value)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
            {
                throw new ArgumentException($"Option {name} requires a value.");
            }

            value = args[i + 1];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private const string HelpText = """
        AiAssistant.Host [options]

        Diagnostics:
          --help, -h                         Show this help
          --version                          Print version
          --smoke-check                      Verify the binary can start
          --startup-check                    Initialize storage and exit
          --capture-smoke [seconds]          Run the capture pipeline headless and report persisted events
          --doctor                           Storage health + model probe + MCP status

        Reports & Q&A:
          --report-range --from <date> --to <date> [--output <dir>]
                                             Generate Markdown + SVG + stats without the UI
          --ask "<question>" [--days 7] [--output <dir>]
                                             Evidence-grounded Q&A over recent local events

        Maintenance:
          --backup <path>                    Write an encrypted backup
          --restore <path> --confirm-restore Restore an encrypted backup
          --purge-data --confirm-purge       Delete all captured local data
          --rebuild-vectors                  Rebuild the embedding index

        Models:
          --benchmark-model [--output <p>]   Run the fixed chat benchmark
          --benchmark-retrieval [--output <p>] Run the retrieval quality benchmark
          --benchmark-asr [--samples <dir>] [--output <p>]
                                             Run the Chinese ASR golden set (wav + txt) and report CER
          --benchmark-ocr [--samples <dir>] [--output <p>]
                                             Run the OCR golden set (png + txt); Linux RapidOCR only
          --benchmark-kws [--samples <dir>] [--output <p>]
                                             Run the wake-word golden set (positive/negative wav)
          --probe-model                      Print endpoint capabilities
          --import-model-secret <name>       Read a secret from stdin and store it encrypted
          --list-model-secrets               List secret names (values are never shown)
          --delete-model-secret <name> --confirm-delete-secret

        Local MCP:
          --mcp-serve                        Run the read-only MCP endpoint headlessly
          --mcp-token                        Print the local bearer token
        """;

    private static DateOnly ParseDateOption(string[] args, string name, DateOnly fallback)
    {
        if (!TryGetOption(args, name, out var value))
        {
            return fallback;
        }

        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new ArgumentException($"Option {name} must use yyyy-MM-dd.");
    }

    private static int ParseIntOption(string[] args, string name, int fallback, int min, int max)
    {
        if (!TryGetOption(args, name, out var value))
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new ArgumentException($"Option {name} must be an integer.");
        }

        return Math.Clamp(parsed, min, max);
    }

    private static string ResolveOutputDirectory(string[] args, string defaultFolder) =>
        TryGetOption(args, "--output", out var value)
            ? Path.GetFullPath(value)
            : Path.GetFullPath(Path.Combine("artifacts", defaultFolder));

    private static string? ReadSecretFromConsole()
    {
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadLine();
        }

        var builder = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }

        return builder.ToString();
    }

    private static string ResolveSamplesDirectory(string[] args, string prefix)
    {
        if (TryGetOption(args, "--samples", out var value))
        {
            return Path.GetFullPath(value);
        }

        return Path.GetFullPath(Path.Combine("artifacts", "golden", prefix));
    }

    private static string ResolveBenchmarkOutput(string[] args, string prefix)
    {
        if (TryGetOption(args, "--output", out var value))
        {
            return Path.GetFullPath(value);
        }

        return Path.GetFullPath(Path.Combine(
            "artifacts",
            "benchmarks",
            $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.md"));
    }

    private static int StartLinuxDesktop(LinuxDisplayBackendState backendState, string[] args)
    {
        var lifetime = new ClassicDesktopStyleApplicationLifetime { Args = args };
        try
        {
            BuildAvaloniaApp(backendState.Decision.Backend).SetupWithLifetime(lifetime);
        }
        catch (Exception ex) when (backendState.TryMarkInitializationFallback(ex.GetType().Name))
        {
            lifetime.Dispose();
            Console.Error.WriteLine($"Native Wayland initialization failed ({ex.GetType().Name}); retrying with X11/XWayland.");
            lifetime = new ClassicDesktopStyleApplicationLifetime { Args = args };
            BuildAvaloniaApp(LinuxDisplayBackendKind.X11).SetupWithLifetime(lifetime);
        }

        try
        {
            return lifetime.Start(args);
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp(LinuxDisplayBackendKind? linuxBackend)
    {
        var builder = AppBuilder.Configure<Desktop.ShellApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        if (OperatingSystem.IsLinux())
        {
            var selectedBackend = linuxBackend ?? LinuxDisplayBackendSelector.Select(LinuxDesktopSessionSnapshot.Detect()).Backend;
            if (selectedBackend == LinuxDisplayBackendKind.Wayland)
            {
                // ADR-0006: Native Wayland is the default; Main retries X11 only after an initialization failure.
                builder = builder.UseWayland();
            }
        }

        return builder;
    }
}
