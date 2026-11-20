using CommunityToolkit.Mvvm.ComponentModel;

namespace AiAssistant.Desktop.ViewModels;

public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private int _selectedSectionIndex;
    private string _captureStatus = "本机加密存储";

    public ShellViewModel(
        TimelineViewModel timeline,
        CaptureControlViewModel capture,
        DiagnosticsViewModel diagnostics)
    {
        Timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        Capture = capture ?? throw new ArgumentNullException(nameof(capture));
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        Capture.StateChanged += OnCaptureStateChanged;
        CaptureStatus = Capture.CaptureStateText;
    }

    public TimelineViewModel Timeline { get; }

    public CaptureControlViewModel Capture { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    public int SelectedSectionIndex
    {
        get => _selectedSectionIndex;
        set => SetProperty(ref _selectedSectionIndex, value);
    }

    public string CaptureStatus
    {
        get => _captureStatus;
        private set => SetProperty(ref _captureStatus, value);
    }

    public bool IsExitRequested { get; set; }

    public async Task InitializeAsync()
    {
        await Capture.InitializeAsync();
        await Timeline.InitializeAsync();
        await Diagnostics.RefreshAsync();
    }

    public void ShowTimeline()
    {
        SelectedSectionIndex = 0;
    }

    public void ShowDiagnostics()
    {
        SelectedSectionIndex = 2;
    }

    public void Dispose()
    {
        Capture.StateChanged -= OnCaptureStateChanged;
        Timeline.Dispose();
        Capture.Dispose();
    }

    private void OnCaptureStateChanged(object? sender, EventArgs e)
    {
        CaptureStatus = Capture.CaptureStateText;
    }
}
