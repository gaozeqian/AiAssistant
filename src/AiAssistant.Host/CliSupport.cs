using System.Globalization;
using System.Text;
using AiAssistant.Core.Configuration;
using AiAssistant.Desktop;
using AiAssistant.Platform.Linux.Desktop;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace AiAssistant.Host;

/// <summary>
/// CLI argument parsing, maintenance helpers and desktop bootstrap shared by the Host entry point.
/// Kept separate from <see cref="Program"/> so command routing stays readable.
/// </summary>
internal static class CliSupport
{
    internal static bool HasOption(string[] args, string name) =>
        Array.Exists(args, argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));

    internal static bool TryGetOption(string[] args, string name, out string value)
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

    internal const string HelpText = """
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
          --backup <path> [--backup-key <key>]
                                             Write a portable encrypted backup; prints a new key when omitted
          --restore <path> --backup-key <key> --confirm-restore
                                             Restore a portable encrypted backup
          --purge-data --confirm-purge       Delete all captured local data
          --rebuild-vectors                  Rebuild the embedding index

        Models:
          --benchmark-model [--output <p>]   Run the fixed chat benchmark
          --probe-model                      Print endpoint capabilities
          --import-model-secret <name>       Read a secret from stdin and store it encrypted
          --list-model-secrets               List secret names (values are never shown)
          --delete-model-secret <name> --confirm-delete-secret

        Local MCP:
          --mcp-serve                        Run the read-only MCP endpoint headlessly
          --mcp-token                        Print the local bearer token

        Developer-only (not shipped): tools/AiAssistant.Diagnostics.Host
          --benchmark-retrieval / --benchmark-asr / --benchmark-ocr / --benchmark-kws / --ui-e2e
        """;

    internal static DateOnly ParseDateOption(string[] args, string name, DateOnly fallback)
    {
        if (!TryGetOption(args, name, out var value))
        {
            return fallback;
        }

        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new ArgumentException($"Option {name} must use yyyy-MM-dd.");
    }

    internal static int ParseIntOption(string[] args, string name, int fallback, int min, int max)
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

    internal static string ResolveOutputDirectory(string[] args, string defaultFolder) =>
        TryGetOption(args, "--output", out var value)
            ? Path.GetFullPath(value)
            : ArtifactPaths.Resolve("test-results", "reports", defaultFolder);

    internal static string? ReadSecretFromConsole()
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

    internal static string ResolveBenchmarkOutput(string[] args, string prefix, Application.Ports.IClock clock)
    {
        if (TryGetOption(args, "--output", out var value))
        {
            return Path.GetFullPath(value);
        }

        return ArtifactPaths.Resolve(
            "test-results",
            "benchmarks",
            $"{prefix}-{clock.UtcNow:yyyyMMdd-HHmmss}.md");
    }

    /// <summary>Arguments that run headless and must never touch the GUI single-instance socket.</summary>
    internal static readonly string[] HeadlessArgumentPrefixes =
    [
        "--smoke-check", "--help", "-h", "--version", "--mcp-token", "--mcp-serve", "--doctor",
        "--probe-model", "--benchmark-", "--report-range", "--ask", "--rebuild-vectors",
        "--backup", "--restore", "--purge-data", "--import-model-secret", "--list-model-secrets",
        "--delete-model-secret", "--capture-smoke", "--startup-check", "--ui-e2e"
    ];

    internal static bool IsGuiInvocation(string[] args) =>
        !args.Any(argument => HeadlessArgumentPrefixes.Any(prefix =>
            argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

    internal static int StartLinuxDesktop(
        LinuxDisplayBackendState backendState,
        string[] args,
        DesktopLaunchContext context)
    {
        var lifetime = new ClassicDesktopStyleApplicationLifetime { Args = args };
        try
        {
            BuildAvaloniaApp(context, backendState.Decision.Backend).SetupWithLifetime(lifetime);
        }
        catch (Exception ex) when (backendState.TryMarkInitializationFallback(ex.GetType().Name))
        {
            lifetime.Dispose();
            Console.Error.WriteLine($"Native Wayland initialization failed ({ex.GetType().Name}); retrying with X11/XWayland.");
            lifetime = new ClassicDesktopStyleApplicationLifetime { Args = args };
            BuildAvaloniaApp(context, LinuxDisplayBackendKind.X11).SetupWithLifetime(lifetime);
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

    public static AppBuilder BuildAvaloniaApp(
        DesktopLaunchContext context,
        LinuxDisplayBackendKind? linuxBackend)
    {
        var builder = AppBuilder.Configure(() => new ShellApp(context))
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
