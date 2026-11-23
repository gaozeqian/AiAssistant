using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>Decouples voice commands from Desktop views without Voice referencing Avalonia.</summary>
public sealed class VoiceNavigationHub : IVoiceViewNavigator
{
    public event EventHandler? TimelineRequested;
    public event EventHandler? ReportsRequested;
    public event EventHandler? ExitRequested;

    public void ShowTimeline() => TimelineRequested?.Invoke(this, EventArgs.Empty);
    public void ShowReports() => ReportsRequested?.Invoke(this, EventArgs.Empty);
    public void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);
}
