using System.Globalization;
using AiAssistant.Application.Ports;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiAssistant.Desktop.ViewModels;

public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private int _selectedSectionIndex;
    private string _captureStatus = "本机加密存储";
    private readonly VoiceNavigationHub? _voiceNavigation;
    private bool _exitArmed;
    private CancellationTokenSource? _exitArmCts;

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
        Diagnostics.VoicePageRequested += OnVoicePageRequested;
        if (Data is not null)
        {
            Data.RestartRequested += OnDataRestartRequested;
        }
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
        set => SetProperty(ref _selectedSectionIndex, value);
    }

    public string CaptureStatus
    {
        get => _captureStatus;
        private set => SetProperty(ref _captureStatus, value);
    }

    public bool IsExitRequested { get; set; }

    /// <summary>True while the exit button waits for the second, confirming click.</summary>
    public bool IsExitArmed => _exitArmed;

    /// <summary>Header button label; changes to 「确认退出」 while armed.</summary>
    public string ExitButtonText => _exitArmed ? "确认退出" : "退出";

    /// <summary>
    /// Explicit exit affordance; closing the window only hides it. Exiting stops capture, so the
    /// first click only arms the button — a second click within five seconds confirms. The control's
    /// AutomationProperties.Name stays 「退出应用」 so headless assertions and screen readers keep working.
    /// </summary>
    public void RequestExit()
    {
        if (!_exitArmed)
        {
            _exitArmed = true;
            OnPropertyChanged(nameof(IsExitArmed));
            OnPropertyChanged(nameof(ExitButtonText));
            _exitArmCts?.Cancel();
            _exitArmCts?.Dispose();
            _exitArmCts = new CancellationTokenSource();
            _ = ResetExitArmAsync(_exitArmCts.Token);
            return;
        }

        CancelExitArm();
        IsExitRequested = true;
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task ResetExitArmAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ct.IsCancellationRequested)
        {
            Dispatcher.UIThread.Post(CancelExitArm);
        }
    }

    private void CancelExitArm()
    {
        _exitArmCts?.Cancel();
        _exitArmCts?.Dispose();
        _exitArmCts = null;
        if (!_exitArmed)
        {
            return;
        }

        _exitArmed = false;
        OnPropertyChanged(nameof(IsExitArmed));
        OnPropertyChanged(nameof(ExitButtonText));
    }

    public async Task InitializeAsync()
    {
        await Capture.InitializeAsync();
        await Timeline.InitializeAsync();
        await Diagnostics.RefreshAsync();
        await Reports.InitializeAsync();
        if (Voice is not null)
        {
            await Voice.InitializeAsync();
        }
    }

    public void ShowTimeline()
    {
        SelectedSectionIndex = ShellSection.Timeline;
    }

    public void ShowSearch()
    {
        SelectedSectionIndex = ShellSection.Search;
    }

    public void ShowVoice()
    {
        SelectedSectionIndex = ShellSection.Voice;
    }

    public void ShowDiagnostics()
    {
        SelectedSectionIndex = ShellSection.Diagnostics;
    }

    public void ShowReports()
    {
        SelectedSectionIndex = ShellSection.Reports;
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
            case ShellSection.Diagnostics:
                await Diagnostics.RefreshCommand.ExecuteAsync(null);
                break;
            case ShellSection.Reports:
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
        _exitArmCts?.Cancel();
        _exitArmCts?.Dispose();
        _exitArmCts = null;
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

        Diagnostics.VoicePageRequested -= OnVoicePageRequested;
        if (Data is not null)
        {
            Data.RestartRequested -= OnDataRestartRequested;
        }

        Timeline.Dispose();
        Capture.Dispose();
        Diagnostics.Dispose();
        Reports.Dispose();
        Data?.Dispose();
        Voice?.Dispose();
    }

    private void OnCaptureStateChanged(object? sender, EventArgs e)
    {
        CaptureStatus = Capture.CaptureStateText;
    }

    private void OnTimelineRequested(object? sender, EventArgs e) => RunOnUiThread(ShowTimeline);

    private void OnReportsRequested(object? sender, EventArgs e) => RunOnUiThread(ShowReports);

    private void OnVoicePageRequested(object? sender, EventArgs e) => RunOnUiThread(ShowVoice);

    private void OnEvidenceNavigationRequested(object? sender, EvidenceNavigationRequest request)
    {
        SelectedSectionIndex = ShellSection.Timeline;
        _ = NavigateTimelineAsync(request);
    }

    /// <summary>
    /// Voice commands run on the audio session thread; every UI-bound mutation (tab index, exit,
    /// restart) has to be marshalled or Avalonia throws "Call from invalid thread" and the session dies.
    /// </summary>
    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
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

    private void OnExitRequested(object? sender, EventArgs e) => RunOnUiThread(() =>
    {
        IsExitRequested = true;
        ExitRequested?.Invoke(this, EventArgs.Empty);
    });

    private void OnRestartRequested() => RestartRequested?.Invoke();

    private void OnDataRestartRequested(object? sender, EventArgs e) => RunOnUiThread(() => RestartRequested?.Invoke());
}
