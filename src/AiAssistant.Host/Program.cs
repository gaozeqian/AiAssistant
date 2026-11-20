using Avalonia;

namespace AiAssistant.Host;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--smoke-check", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("AiAssistant.Host smoke-check OK");
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<Desktop.ShellApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        if (OperatingSystem.IsLinux())
        {
            var forcedBackend = Environment.GetEnvironmentVariable("AIASSISTANT_LINUX_BACKEND");
            var preferX11 = string.Equals(forcedBackend, "X11", StringComparison.OrdinalIgnoreCase);
            if (!preferX11)
            {
                // ADR-0006: Native Wayland is the default on Linux, falling back on init error or explicit X11 override.
                builder = builder.UseWayland();
            }
        }

        return builder;
    }
}
