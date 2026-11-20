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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AiAssistant startup failed ({ex.GetType().Name}). Check storage permissions and available disk space.");
            return 2;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(null);

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
