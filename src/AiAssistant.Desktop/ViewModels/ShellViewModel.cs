using AiAssistant.Application.Ports;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiAssistant.Desktop.ViewModels;

public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private int _selectedSectionIndex;
    private string _captureStatus = "本机加密存储";
    private readonly VoiceNavigationHub? _voiceNavigation;

    public ShellViewModel(
        TimelineViewModel timeline,
        CaptureControlViewModel capture,
        DiagnosticsViewModel diagnostics,
        ReportsViewModel reports,
        VoiceControlViewModel? voice = null,
        VoiceNavigationHub? voiceNavigation = null)
    {
        Timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        Capture = capture ?? throw new ArgumentNullException(nameof(capture));
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        Reports = reports ?? throw new ArgumentNullException(nameof(reports));
        Voice = voice;
        _voiceNavigation = voiceNavigation;
        Capture.StateChanged += OnCaptureStateChanged;
        CaptureStatus = Capture.CaptureStateText;
        if (_voiceNavigation is not null)
        {
            _voiceNavigation.TimelineRequested += OnTimelineRequested;
            _voiceNavigation.ReportsRequested += OnReportsRequested;
            _voiceNavigation.ExitRequested += OnExitRequested;
        }
    }

    public TimelineViewModel Timeline { get; }

    public CaptureControlViewModel Capture { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    public ReportsViewModel Reports { get; }

    public VoiceControlViewModel? Voice { get; }

    public event EventHandler? ExitRequested;

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
        await Reports.InitializeAsync();
    }

    public void ShowTimeline()
    {
        SelectedSectionIndex = 0;
    }

    public void ShowDiagnostics()
    {
        SelectedSectionIndex = 3;
    }

    public void ShowReports()
    {
        SelectedSectionIndex = 4;
    }

    public void Dispose()
    {
        Capture.StateChanged -= OnCaptureStateChanged;
        if (_voiceNavigation is not null)
        {
            _voiceNavigation.TimelineRequested -= OnTimelineRequested;
            _voiceNavigation.ReportsRequested -= OnReportsRequested;
            _voiceNavigation.ExitRequested -= OnExitRequested;
        }

        Timeline.Dispose();
        Capture.Dispose();
        Voice?.Dispose();
    }

    private void OnCaptureStateChanged(object? sender, EventArgs e)
    {
        CaptureStatus = Capture.CaptureStateText;
    }

    private void OnTimelineRequested(object? sender, EventArgs e) => ShowTimeline();

    private void OnReportsRequested(object? sender, EventArgs e) => ShowReports();

    private void OnExitRequested(object? sender, EventArgs e)
    {
        IsExitRequested = true;
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }
}
