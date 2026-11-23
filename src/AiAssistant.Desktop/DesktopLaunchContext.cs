using AiAssistant.Desktop.ViewModels;
using AiAssistant.Desktop.Views;

namespace AiAssistant.Desktop;

/// <summary>
/// Immutable startup context injected into <see cref="ShellApp"/> by the composition root.
/// Replaces the former static mutable properties so tests and alternate hosts cannot leak state
/// between application instances.
/// </summary>
public sealed record DesktopLaunchContext(
    Func<ShellViewModel> ViewModelFactory,
    string? InstanceSocketPath,
    bool TrayAvailable,
    Func<MainWindow, ShellViewModel, Task<Exception?>, Action<int>, string, Task<int>>? UiE2ERunner = null);
