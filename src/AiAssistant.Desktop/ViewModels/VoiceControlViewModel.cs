using AiAssistant.Application.Ports;
using AiAssistant.Core.ValueObjects;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiAssistant.Desktop.ViewModels;

public sealed class VoiceControlViewModel : ObservableObject, IDisposable
{
    private readonly IVoiceSession _session;
    private bool _isEnabled;
    private bool _isBusy;
    private VoiceSessionState _lastState = VoiceSessionState.Sleeping;
    private string _statusText = "语音休眠";
    private string _capabilityText = "尚未检查语音能力";
    private string _lastTranscript = "尚无语音命令";
    private string _lastFeedback = "启用后说“你好时光”唤醒";
    private string _errorMessage = string.Empty;
    private bool _recordsCommandAudio;
    private string _commandAudioStatus = "命令音频仅驻留内存，不落盘。";
    private bool _disposed;

    public VoiceControlViewModel(IVoiceSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ToggleCommand = new AsyncRelayCommand(ToggleAsync);
        ToggleCommandAudioCommand = new AsyncRelayCommand(ToggleCommandAudioAsync);
        _session.StateChanged += OnStateChanged;
        _session.Interaction += OnInteraction;
        _isEnabled = _session.IsActive;
        UpdateStatus(_session.State);
        CapabilityText = _session.Capability.Summary;
        RecordsCommandAudio = _session.RecordsCommandAudio;
        CommandAudioStatus = DescribeCommandAudio(RecordsCommandAudio);
    }

    public IAsyncRelayCommand ToggleCommand { get; }

    public IAsyncRelayCommand ToggleCommandAudioCommand { get; }

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.StateChanged -= OnStateChanged;
        _session.Interaction -= OnInteraction;
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
        ? "命令音频已开启：唤醒后的命令音频会以 AES-GCM 加密保存，并在保留期结束后自动擦除。"
        : "命令音频仅驻留内存，不落盘。";

    private void OnStateChanged(object? sender, VoiceSessionState state)
    {
        if (_disposed)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateStatus(state);
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                {
                    UpdateStatus(state);
                }
            });
        }
    }

    private void OnInteraction(object? sender, VoiceInteraction interaction)
    {
        if (_disposed)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyInteraction(interaction);
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                {
                    ApplyInteraction(interaction);
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
    }
}
