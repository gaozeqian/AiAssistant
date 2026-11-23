using Avalonia;
using Avalonia.Headless;

namespace AiAssistant.Desktop.Tests;

public static class DesktopTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Avalonia.Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
