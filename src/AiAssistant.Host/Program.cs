using System.Globalization;
using System.Reflection;
using System.Text;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.Configuration;
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
        catch (IOException ex)
        {
            // No console attached (service/tray mode); keep default encoding.
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Host.Program.Main", ex);
        }

        if (args.Contains("--smoke-check", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("AiAssistant.Host smoke-check OK");
            return 0;
        }

        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(CliSupport.HelpText);
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
                // Closing the window hides it (capture keeps running); launching again should
                // surface the existing window instead of starting a second recorder.
                if (CliSupport.IsGuiInvocation(args) &&
                    Desktop.SingleInstance.TryActivateExisting(runtime.InstanceSocketPath))
                {
                    Console.WriteLine("AiAssistant 已在运行，已请求显示主窗口。");
                    return 0;
                }

                var handoff = new DesktopShellHandoff();
                var exitCode = RunAsync(runtime, args, handoff).GetAwaiter().GetResult();
                if (!handoff.Requested)
                {
                    return exitCode;
                }

                // 外部 AX 驱动测试可请求显示窗口；内置 UI 自驱动已迁到 tools/AiAssistant.Diagnostics.Host。
                if (args.Contains("--show-window", StringComparer.OrdinalIgnoreCase))
                {
                    Environment.SetEnvironmentVariable(EnvironmentNames.UiE2EShow, "1");
                }

                if (CliSupport.TryGetOption(args, "--data-directory", out var dataDirectory))
                {
                    Environment.SetEnvironmentVariable(EnvironmentNames.DataDirectory, dataDirectory);
                }

                Desktop.ShellApp.TrayAvailable = runtime.IsSystemTraySupported;
                Desktop.ShellApp.InstanceSocketPath = runtime.InstanceSocketPath;
                Desktop.ShellApp.ViewModelFactory = runtime.CreateShellViewModel;
                return OperatingSystem.IsLinux() && runtime.LinuxBackendState is { } linuxBackend
                    ? CliSupport.StartLinuxDesktop(linuxBackend, args)
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
                catch (TimeoutException ex)
                {
                    // Bounded shutdown: the OS reclaims remaining resources when the process exits.
                    AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Host.Program.Main", ex);
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
                catch (OperationCanceledException ex)
                {
                    // Normal Ctrl+C shutdown
                    AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Host.Program.RunAsync", ex);
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
                    catch (OperationCanceledException ex)
                    {
                        // Helper start timed out; the status line below still reports the supervisor state.
                        AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Host.Program.RunAsync", ex);
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

            if (CliSupport.TryGetOption(args, "--import-model-secret", out var secretName))
            {
                Console.Error.Write($"Secret value for '{secretName}': ");
                var secretValue = CliSupport.ReadSecretFromConsole();
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

            if (CliSupport.TryGetOption(args, "--delete-model-secret", out var deleteSecretName))
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
                var outputPath = CliSupport.ResolveBenchmarkOutput(args, "model");
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await File.WriteAllTextAsync(outputPath, report.ToMarkdown(), CancellationToken.None);
                Console.WriteLine(
                    $"Model benchmark: model={report.ChatModel} success={report.SuccessRate:P0} json={report.JsonComplianceRate:P0} avg={report.AverageLatencyMilliseconds}ms");
                Console.WriteLine($"report={outputPath}");
                return report.SuccessRate > 0 ? 0 : 1;
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
                var today = DateOnly.FromDateTime(TimeProvider.System.GetLocalNow().LocalDateTime.Date);
                var from = CliSupport.ParseDateOption(args, "--from", today.AddDays(-6));
                var to = CliSupport.ParseDateOption(args, "--to", today);
                var outputDirectory = CliSupport.ResolveOutputDirectory(args, "reports");
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

            if (CliSupport.TryGetOption(args, "--ask", out var question))
            {
                var days = CliSupport.ParseIntOption(args, "--days", 7, 1, 366);
                var answer = await runtime.EvidenceSearch.AskRecentAsync(question, days, CancellationToken.None);
                Console.WriteLine(answer.Answer);
                Console.WriteLine(
                    $"citations={string.Join(",", answer.CitedEvidenceIds)} model={answer.ModelId} degraded={answer.Degraded}");
                foreach (var evidence in answer.Evidence)
                {
                    Console.WriteLine(
                        $"[{evidence.EvidenceId}] {evidence.LocalDate:yyyy-MM-dd} {evidence.AppName} | {evidence.Title}");
                }

                if (CliSupport.TryGetOption(args, "--output", out var exportDirectory))
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

            if (CliSupport.TryGetOption(args, "--backup", out var backupPath))
            {
                await runtime.DatabaseMaintenance.BackupEncryptedAsync(Path.GetFullPath(backupPath), CancellationToken.None);
                Console.WriteLine($"AiAssistant encrypted backup written to {Path.GetFullPath(backupPath)}");
                return 0;
            }

            if (CliSupport.TryGetOption(args, "--restore", out var restorePath))
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
            if (CliSupport.TryGetOption(args, "--capture-smoke", out var captureSecondsText))
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

    public static AppBuilder BuildAvaloniaApp() => CliSupport.BuildAvaloniaApp(null);

}
