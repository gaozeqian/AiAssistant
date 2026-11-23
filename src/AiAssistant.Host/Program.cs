using System.Reflection;
using System.Text;
using AiAssistant.Application.Diagnostics;
using AiAssistant.Core.Configuration;
using AiAssistant.Host.Commands;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace AiAssistant.Host;

public static class Program
{
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
            SuppressedExceptionLog.Trace("Host.Program.Main", ex);
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

                var result = CliCommandRouter
                    .ExecuteAsync(runtime, args, CancellationToken.None)
                    .GetAwaiter().GetResult();
                if (!result.StartDesktop)
                {
                    return result.ExitCode;
                }

                // 外部 AX 驱动测试可请求显示窗口；内置 UI 自驱动在 Diagnostics.Host。
                if (CliSupport.HasOption(args, "--show-window"))
                {
                    Environment.SetEnvironmentVariable(EnvironmentNames.UiE2EShow, "1");
                }

                if (CliSupport.TryGetOption(args, "--data-directory", out var dataDirectory))
                {
                    Environment.SetEnvironmentVariable(EnvironmentNames.DataDirectory, dataDirectory);
                }

                var launchContext = new Desktop.DesktopLaunchContext(
                    runtime.CreateShellViewModel,
                    runtime.InstanceSocketPath,
                    runtime.IsSystemTraySupported);
                return OperatingSystem.IsLinux() && runtime.LinuxBackendState is { } linuxBackend
                    ? CliSupport.StartLinuxDesktop(linuxBackend, args, launchContext)
                    : BuildAvaloniaApp(launchContext).StartWithClassicDesktopLifetime(args);
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
                    SuppressedExceptionLog.Trace("Host.Program.Main", ex);
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

    public static AppBuilder BuildAvaloniaApp(Desktop.DesktopLaunchContext context) =>
        CliSupport.BuildAvaloniaApp(context, null);
}
