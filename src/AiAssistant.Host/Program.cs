using System.Text;
using AiAssistant.Host.Benchmarks;
using AiAssistant.Platform.Linux.Desktop;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace AiAssistant.Host;

public static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--smoke-check", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("AiAssistant.Host smoke-check OK");
            return 0;
        }

        try
        {
            await using var runtime = await AppRuntime.CreateAsync();

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

            var isMaintenanceCommand = args.Any(argument =>
                string.Equals(argument, "--backup", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "--restore", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "--purge-data", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "--rebuild-vectors", StringComparison.OrdinalIgnoreCase));
            if (isMaintenanceCommand)
            {
                // Maintenance commands bypass StartAsync, so the encrypted schema must be initialized explicitly.
                await runtime.DatabaseMaintenance.InitializeAsync(CancellationToken.None);
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
            if (args.Contains("--startup-check", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"AiAssistant.Host startup-check OK; recovered jobs: {runtime.RecoveredJobCount}");
                return 0;
            }

            Desktop.ShellApp.ViewModelFactory = runtime.CreateShellViewModel;
            if (OperatingSystem.IsLinux() && runtime.LinuxBackendState is { } linuxBackend)
            {
                return StartLinuxDesktop(linuxBackend, args);
            }

            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"AiAssistant usage error: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AiAssistant startup failed ({ex.GetType().Name}). Check storage permissions and available disk space.");
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
