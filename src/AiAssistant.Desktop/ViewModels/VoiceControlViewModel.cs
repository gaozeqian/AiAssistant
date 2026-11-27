using System.Collections.ObjectModel;
using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiAssistant.Desktop.ViewModels;

public sealed class VoiceControlViewModel : ObservableObject, IDisposable
{
    /// <summary>The page is a review surface, not a log browser; keep the list short and scannable.</summary>
    public const int CommandHistoryLimit = 20;

    private readonly IVoiceSession _session;
    private readonly IVoiceCommandLog? _commandLog;
    private readonly ICommandAudioPlayer? _commandAudioPlayer;
    private bool _isEnabled;
    private bool _isBusy;
    private bool _isReplaying;
    private VoiceSessionState _lastState = VoiceSessionState.Sleeping;
    private string _statusText = "语音休眠";
    private string _capabilityText = "尚未检查语音能力";
    private string _lastTranscript = "尚无语音命令";
    private string _lastFeedback = "启用后说“你好时光”唤醒";
    private string _errorMessage = string.Empty;
    private bool _recordsCommandAudio;
    private string _commandAudioStatus = "命令音频仅驻留内存，不落盘。";
    private string _commandHistoryStatus = "尚无命令记录";
    private bool _refreshingHistory;
    private bool _historyRefreshPending;
    private CancellationTokenSource? _replayCts;
    private bool _disposed;

    public VoiceControlViewModel(
        IVoiceSession session,
        IVoiceCommandLog? commandLog = null,
        ICommandAudioPlayer? commandAudioPlayer = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _commandLog = commandLog;
        _commandAudioPlayer = commandAudioPlayer;
        ToggleCommand = new AsyncRelayCommand(ToggleAsync);
        ToggleCommandAudioCommand = new AsyncRelayCommand(ToggleCommandAudioAsync);
        RefreshHistoryCommand = new AsyncRelayCommand(RefreshHistoryAsync);
        // Replay must not serialize the whole list behind one command: a slow clip would grey out
        // every row. Each row carries its own "playing" state and a new click supersedes the old clip.
        ReplayCommand = new AsyncRelayCommand<VoiceCommandHistoryItem?>(
            ReplayAsync,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        _session.StateChanged += OnStateChanged;
        _session.Interaction += OnInteraction;
        _session.Faulted += OnFaulted;
        _isEnabled = _session.IsActive;
        UpdateStatus(_session.State);
        RecordsCommandAudio = _session.RecordsCommandAudio;
        CommandAudioStatus = DescribeCommandAudio(RecordsCommandAudio);
    }

    public ObservableCollection<VoiceCommandHistoryItem> CommandHistory { get; } = [];

    public IAsyncRelayCommand ToggleCommand { get; }

    public IAsyncRelayCommand ToggleCommandAudioCommand { get; }

    public IAsyncRelayCommand RefreshHistoryCommand { get; }

    public IAsyncRelayCommand<VoiceCommandHistoryItem?> ReplayCommand { get; }

    public bool HasCommandHistory => CommandHistory.Count > 0;

    /// <summary>Explicit opt-in reflected from the session; command audio is never persisted by default.</summary>
    public bool RecordsCommandAudio
    {
        get => _recordsCommandAudio;
        private set
        {
            if (SetProperty(ref _recordsCommandAudio, value))
            {
                OnPropertyChanged(nameof(CommandAudioStatus));
            }
        }
    }

    public string CommandAudioStatus
    {
        get => _commandAudioStatus;
        private set => SetProperty(ref _commandAudioStatus, value);
    }

    public string CommandHistoryStatus
    {
        get => _commandHistoryStatus;
        private set => SetProperty(ref _commandHistoryStatus, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        private set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                // Enabling does not change the session state (it stays Sleeping until the wake word),
                // so the label must be refreshed here to distinguish "off" from "armed".
                UpdateStatus(_lastState);
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsReplaying
    {
        get => _isReplaying;
        private set => SetProperty(ref _isReplaying, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CapabilityText
    {
        get => _capabilityText;
        private set => SetProperty(ref _capabilityText, value);
    }

    public string LastTranscript
    {
        get => _lastTranscript;
        private set => SetProperty(ref _lastTranscript, value);
    }

    public string LastFeedback
    {
        get => _lastFeedback;
        private set => SetProperty(ref _lastFeedback, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Loads the persisted command history; called by the shell once the window exists.</summary>
    public Task InitializeAsync() => RefreshHistoryAsync();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _replayCts?.Cancel();
        _session.StateChanged -= OnStateChanged;
        _session.Interaction -= OnInteraction;
        _session.Faulted -= OnFaulted;
    }

    private async Task ToggleAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            if (IsEnabled)
            {
                await _session.StopAsync(CancellationToken.None);
                IsEnabled = false;
            }
            else
            {
                await _session.StartAsync(CancellationToken.None);
                // The session can refuse to arm (for example missing wake-word assets); the switch
                // must follow what actually happened, not the capability alone.
                IsEnabled = _session.IsActive;
                CapabilityText = _session.Capability.Summary;
                if (!IsEnabled)
                {
                    ErrorMessage = _session.Capability.RemediationHint ?? "语音模型或设备不可用。";
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or AiAssistant.Core.Exceptions.AiAssistantDomainException)
        {
            ErrorMessage = "语音切换失败，请检查麦克风权限和模型资产。";
            // Revert the optimistic toggle so the switch reflects the real (unchanged) state.
            OnPropertyChanged(nameof(IsEnabled));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ToggleCommandAudioAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await _session.SetCommandAudioRecordingAsync(!RecordsCommandAudio, CancellationToken.None);
            RecordsCommandAudio = _session.RecordsCommandAudio;
            CommandAudioStatus = DescribeCommandAudio(RecordsCommandAudio);
        }
        catch (InvalidOperationException)
        {
            ErrorMessage = "当前会话未接入加密媒体库，无法记录命令音频。";
        }
        catch (AiAssistant.Core.Exceptions.AiAssistantDomainException)
        {
            ErrorMessage = "命令音频设置失败，请检查存储健康状态。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string DescribeCommandAudio(bool enabled) => enabled
        ? "命令音频已开启：唤醒后的命令音频会以 AES-GCM 加密保存，按「数据与导出 → 保留与清理」中的保留期自动擦除；可在下方记录中重听。"
        : "命令音频仅驻留内存，不落盘。";

    private async Task RefreshHistoryAsync()
    {
        // Interactions arrive in bursts (wake chime, transcript, feedback). Coalesce them into one
        // in-flight query plus at most one follow-up so the newest command is never dropped.
        if (_commandLog is null || _disposed)
        {
            return;
        }

        if (_refreshingHistory)
        {
            _historyRefreshPending = true;
            return;
        }

        _refreshingHistory = true;
        try
        {
            do
            {
                _historyRefreshPending = false;
                var records = await _commandLog.ListRecentAsync(CommandHistoryLimit, CancellationToken.None);
                if (_disposed)
                {
                    return;
                }

                CommandHistory.Clear();
                foreach (var record in records)
                {
                    CommandHistory.Add(new VoiceCommandHistoryItem(record));
                }

                CommandHistoryStatus = records.Count == 0
                    ? "尚无命令记录"
                    : BuildHistoryStatus();
                OnPropertyChanged(nameof(HasCommandHistory));
            }
            while (_historyRefreshPending && !_disposed);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or AiAssistant.Core.Exceptions.AiAssistantDomainException)
        {
            ErrorMessage = "读取命令记录失败，请检查加密数据库状态。";
        }
        finally
        {
            _refreshingHistory = false;
        }
    }

    private async Task ReplayAsync(VoiceCommandHistoryItem? item)
    {
        if (item is null || _disposed)
        {
            return;
        }

        if (!item.HasAudio)
        {
            ErrorMessage = item.WasAudioCleared
                ? "该命令的音频已按保留期清理，无法重听。"
                : "该命令没有留存的音频（记录未开启或已过期）。";
            return;
        }

        if (_commandAudioPlayer is null)
        {
            ErrorMessage = "当前环境不支持重听命令音频。";
            return;
        }

        // A new click supersedes the clip in flight. Combined with per-row state this guarantees a
        // slow or wedged device can never leave every replay button disabled.
        var cts = new CancellationTokenSource();
        _replayCts?.Cancel();
        _replayCts = cts;
        foreach (var entry in CommandHistory)
        {
            entry.IsPlaying = false;
        }

        item.IsPlaying = true;
        IsReplaying = true;
        ErrorMessage = string.Empty;
        try
        {
            var played = await _commandAudioPlayer.PlayAsync(item.Record.AudioContentId!, cts.Token);
            if (!ReferenceEquals(_replayCts, cts))
            {
                // A newer replay took over and owns the status line.
                return;
            }

            if (played)
            {
                CommandHistoryStatus = $"已重听 {item.TimeText} 的命令音频";
            }
            else
            {
                item.MarkAudioCleared();
                CommandHistoryStatus = "音频已过期或被擦除。";
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer replay, or the page was closed while the clip was playing.
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or AiAssistant.Core.Exceptions.AiAssistantDomainException)
        {
            if (ReferenceEquals(_replayCts, cts))
            {
                ErrorMessage = "重听失败，请检查音频设备与存储状态。";
            }
        }
        finally
        {
            item.IsPlaying = false;
            if (ReferenceEquals(_replayCts, cts))
            {
                _replayCts = null;
                IsReplaying = false;
            }

            cts.Dispose();
        }
    }

    private string BuildHistoryStatus()
    {
        var cleared = CommandHistory.Count(entry => entry.WasAudioCleared);
        return cleared == 0
            ? $"最近 {CommandHistory.Count} 条（新→旧）"
            : $"最近 {CommandHistory.Count} 条（新→旧）· {cleared} 条音频已清理";
    }

    private void OnStateChanged(object? sender, VoiceSessionState state) =>
        DispatchToUi(() => UpdateStatus(state));

    private void OnInteraction(object? sender, VoiceInteraction interaction) =>
        DispatchToUi(() => ApplyInteraction(interaction));

    private void OnFaulted(object? sender, VoiceSessionFault fault) =>
        DispatchToUi(() => ApplyFault(fault));

    private void DispatchToUi(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                {
                    action();
                }
            });
        }
    }

    private void ApplyInteraction(VoiceInteraction interaction)
    {
        if (!string.IsNullOrWhiteSpace(interaction.Transcript))
        {
            LastTranscript = interaction.Transcript;
        }

        LastFeedback = interaction.Feedback;
        CapabilityText = _session.Capability.Summary;
        // A new command was persisted (or rejected); keep the review list in step with the session.
        _ = RefreshHistoryAsync();
    }

    private void ApplyFault(VoiceSessionFault fault)
    {
        // The pump is gone: the switch must show "off" and tell the user what to do, instead of
        // leaving the page claiming it is armed while the microphone is closed.
        IsEnabled = false;
        StatusText = "语音已停止";
        CapabilityText = _session.Capability.Summary;
        ErrorMessage = $"{fault.Reason}；请重新启用语音。";
    }

    private void UpdateStatus(VoiceSessionState state)
    {
        _lastState = state;
        StatusText = state switch
        {
            VoiceSessionState.Sleeping => IsEnabled ? "已启用 · 待唤醒" : "语音休眠",
            VoiceSessionState.Listening => "正在聆听",
            VoiceSessionState.Processing => "正在处理",
            VoiceSessionState.Responding => "正在反馈",
            _ => "语音未知状态"
        };
        // The capability summary now distinguishes "ready but disabled" from "armed", so it has to
        // follow the session state instead of being captured once in the constructor.
        CapabilityText = _session.Capability.Summary;
    }
}
