using Avalonia;
using Avalonia.Headless;

namespace AiAssistant.Desktop.Tests;

public static class DesktopTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessShellApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>
/// Headless application that applies the real shell theme (Fluent control themes + semantic
/// brushes). A bare <see cref="Application"/> has no control templates, so windows never build
/// their visual tree and every window-level UI assertion fails.
/// </summary>
public sealed class HeadlessShellApplication : Avalonia.Application
{
    public override void Initialize() => ShellTheme.Apply(this);
}
