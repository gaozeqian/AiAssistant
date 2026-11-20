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
    private string _statusText = "语音休眠";
    private string _capabilityText = "尚未检查语音能力";
    private string _lastTranscript = "尚无语音命令";
    private string _lastFeedback = "启用后说“你好时光”唤醒";
    private string _errorMessage = string.Empty;
    private bool _disposed;

    public VoiceControlViewModel(IVoiceSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ToggleCommand = new AsyncRelayCommand(ToggleAsync);
        _session.StateChanged += OnStateChanged;
        _session.Interaction += OnInteraction;
        UpdateStatus(_session.State);
        CapabilityText = _session.Capability.Summary;
    }

    public IAsyncRelayCommand ToggleCommand { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        private set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(ToggleButtonText));
            }
        }
    }

    public string ToggleButtonText => IsEnabled ? "关闭语音" : "启用语音";

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
                IsEnabled = _session.Capability.IsOperational;
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
        }
        finally
        {
            IsBusy = false;
        }
    }

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
        StatusText = state switch
        {
            VoiceSessionState.Sleeping => "语音休眠",
            VoiceSessionState.Listening => "正在聆听",
            VoiceSessionState.Processing => "正在处理",
            VoiceSessionState.Responding => "正在反馈",
            _ => "语音未知状态"
        };
    }
}
