using System.Globalization;
using AiAssistant.Application.Ports;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
        VoiceNavigationHub? voiceNavigation = null,
        SearchViewModel? search = null,
        DataManagementViewModel? data = null)
    {
        Timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        Capture = capture ?? throw new ArgumentNullException(nameof(capture));
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        Reports = reports ?? throw new ArgumentNullException(nameof(reports));
        Voice = voice;
        Search = search;
        Data = data;
        _voiceNavigation = voiceNavigation;
        SelectSectionCommand = new RelayCommand<string>(SelectSection);
        ShowSearchCommand = new RelayCommand(ShowSearch);
        RefreshCurrentCommand = new AsyncRelayCommand(RefreshCurrentAsync);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        RequestExitCommand = new RelayCommand(RequestExit);
        Capture.StateChanged += OnCaptureStateChanged;
        CaptureStatus = Capture.CaptureStateText;
        if (_voiceNavigation is not null)
        {
            _voiceNavigation.TimelineRequested += OnTimelineRequested;
            _voiceNavigation.ReportsRequested += OnReportsRequested;
            _voiceNavigation.ExitRequested += OnExitRequested;
        }

        if (Search is not null)
        {
            Search.NavigationRequested += OnEvidenceNavigationRequested;
        }

        Diagnostics.ModelConfig.RestartRequested += OnRestartRequested;
    }

    public TimelineViewModel Timeline { get; }

    public CaptureControlViewModel Capture { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    public ReportsViewModel Reports { get; }

    public VoiceControlViewModel? Voice { get; }

    public SearchViewModel? Search { get; }

    public DataManagementViewModel? Data { get; }

    public IRelayCommand<string> SelectSectionCommand { get; }

    public IRelayCommand ShowSearchCommand { get; }

    public IAsyncRelayCommand RefreshCurrentCommand { get; }

    public IRelayCommand ToggleThemeCommand { get; }

    public IRelayCommand RequestExitCommand { get; }

    public event EventHandler? ExitRequested;

    public event Action? RestartRequested;

    public event Action<ThemeVariant>? ThemeVariantChanged;

    /// <summary>Current UI theme variant; the shell applies it to the application on change.</summary>
    private ThemeVariant _themeVariant = ThemeVariant.Light;
    public ThemeVariant ThemeVariant
    {
        get => _themeVariant;
        set
        {
            if (SetProperty(ref _themeVariant, value))
            {
                OnPropertyChanged(nameof(ThemeToggleText));
                ThemeVariantChanged?.Invoke(value);
            }
        }
    }

    /// <summary>The mode this button switches TO (not the current mode).</summary>
    public string ThemeToggleText => ThemeVariant == ThemeVariant.Dark ? "浅色模式" : "深色模式";

    private void ToggleTheme()
    {
        ThemeVariant = ThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    public int SelectedSectionIndex
    {
        get => _selectedSectionIndex;
        set
        {
            if (SetProperty(ref _selectedSectionIndex, value))
            {
                ResetInactiveLoadingStates();
            }
        }
    }

    /// <summary>
    /// Clearing the loading flag of panels not currently shown avoids the Avalonia indeterminate
    /// ProgressBar freezing after a tab is hidden and re-shown.
    /// </summary>
    private void ResetInactiveLoadingStates()
    {
        Search?.ResetBusy();
        Reports?.ResetBusy();
    }

    public string CaptureStatus
    {
        get => _captureStatus;
        private set => SetProperty(ref _captureStatus, value);
    }

    public bool IsExitRequested { get; set; }

    /// <summary>Explicit exit affordance; closing the window only hides it.</summary>
    public void RequestExit()
    {
        IsExitRequested = true;
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

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

    public void ShowSearch()
    {
        SelectedSectionIndex = 1;
    }

    public void ShowDiagnostics()
    {
        SelectedSectionIndex = 5;
    }

    public void ShowReports()
    {
        SelectedSectionIndex = 6;
    }

    private void SelectSection(string? value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
            index >= 0 && index <= 6)
        {
            SelectedSectionIndex = index;
        }
    }

    private async Task RefreshCurrentAsync()
    {
        switch (SelectedSectionIndex)
        {
            case 5:
                await Diagnostics.RefreshCommand.ExecuteAsync(null);
                break;
            case 6:
                await Reports.RefreshCommand.ExecuteAsync(null);
                break;
            default:
                await Timeline.RefreshCommand.ExecuteAsync(null);
                break;
        }
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

        if (Search is not null)
        {
            Search.NavigationRequested -= OnEvidenceNavigationRequested;
            Search.Dispose();
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

    private void OnEvidenceNavigationRequested(object? sender, EvidenceNavigationRequest request)
    {
        SelectedSectionIndex = 0;
        _ = NavigateTimelineAsync(request);
    }

    private async Task NavigateTimelineAsync(EvidenceNavigationRequest request)
    {
        try
        {
            await Timeline.NavigateToEventAsync(request.LocalDate, request.EventId);
        }
        catch (OperationCanceledException ex)
        {
            // 导航期间取消属预期，静默降级。
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Desktop.ShellViewModel.NavigateTimelineAsync", ex);
        }
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        IsExitRequested = true;
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnRestartRequested() => RestartRequested?.Invoke();
}
