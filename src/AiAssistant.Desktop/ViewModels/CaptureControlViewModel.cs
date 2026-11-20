using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.Exceptions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiAssistant.Desktop.ViewModels;

public sealed class CaptureControlViewModel : ObservableObject, IDisposable
{
    private readonly CaptureControlUseCase _capture;
    private bool _isPaused;
    private bool _isBusy;
    private bool _autoStartEnabled;
    private string _blacklistText = string.Empty;
    private string _statusMessage = "采集仅保存在本机加密数据库中";
    private string _metricsText = string.Empty;
    private string _autoStartText = "尚未启用开机自启";
    private bool _disposed;

    public CaptureControlViewModel(CaptureControlUseCase capture)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        TogglePauseCommand = new AsyncRelayCommand(TogglePauseAsync);
        SaveBlacklistCommand = new AsyncRelayCommand(SaveBlacklistAsync);
        CaptureSnapshotCommand = new AsyncRelayCommand(CaptureSnapshotAsync);
        ToggleAutoStartCommand = new AsyncRelayCommand(ToggleAutoStartAsync);
        BlacklistText = string.Join(Environment.NewLine, _capture.BlacklistedPatterns);
        _capture.MetricsUpdated += OnMetricsUpdated;
        ApplyMetrics(_capture.GetMetrics());
    }

    public event EventHandler? StateChanged;

    public IAsyncRelayCommand TogglePauseCommand { get; }

    public IAsyncRelayCommand SaveBlacklistCommand { get; }

    public IAsyncRelayCommand CaptureSnapshotCommand { get; }

    public IAsyncRelayCommand ToggleAutoStartCommand { get; }

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetProperty(ref _isPaused, value))
            {
                OnPropertyChanged(nameof(PauseButtonText));
                OnPropertyChanged(nameof(CaptureStateText));
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string PauseButtonText => IsPaused ? "恢复采集" : "暂停采集";

    public string CaptureStateText => IsPaused ? "已暂停 · 不补采暂停期间内容" : "正在本机采集";

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool AutoStartEnabled
    {
        get => _autoStartEnabled;
        private set
        {
            if (SetProperty(ref _autoStartEnabled, value))
            {
                AutoStartText = value ? "已启用当前用户开机自启" : "尚未启用开机自启";
            }
        }
    }

    public string BlacklistText
    {
        get => _blacklistText;
        set => SetProperty(ref _blacklistText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string MetricsText
    {
        get => _metricsText;
        private set => SetProperty(ref _metricsText, value);
    }

    public string AutoStartText
    {
        get => _autoStartText;
        private set => SetProperty(ref _autoStartText, value);
    }

    public async Task InitializeAsync()
    {
        AutoStartEnabled = await _capture.IsAutoStartEnabledAsync();
        ApplyMetrics(_capture.GetMetrics());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _capture.MetricsUpdated -= OnMetricsUpdated;
    }

    private async Task TogglePauseAsync()
    {
        await RunSafelyAsync(async () =>
        {
            await _capture.TogglePauseAsync();
            ApplyMetrics(_capture.GetMetrics());
            StatusMessage = IsPaused ? "已暂停，恢复后不会补采此期间的数据" : "已恢复本机采集";
        });
    }

    private async Task SaveBlacklistAsync()
    {
        await RunSafelyAsync(async () =>
        {
            var patterns = BlacklistText.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await _capture.UpdateBlacklistAsync(patterns);
            BlacklistText = string.Join(Environment.NewLine, _capture.BlacklistedPatterns);
            StatusMessage = "黑名单已生效；匹配的应用或窗口不会写入事件";
        });
    }

    private async Task CaptureSnapshotAsync()
    {
        await RunSafelyAsync(async () =>
        {
            await _capture.CaptureSnapshotNowAsync();
            StatusMessage = IsPaused ? "当前已暂停，未生成快照" : "快照已提交，返回时间线刷新即可查看";
        });
    }

    private async Task ToggleAutoStartAsync()
    {
        await RunSafelyAsync(async () =>
        {
            var enabled = !AutoStartEnabled;
            await _capture.SetAutoStartEnabledAsync(enabled);
            AutoStartEnabled = await _capture.IsAutoStartEnabledAsync();
            StatusMessage = AutoStartEnabled == enabled ? "开机自启设置已更新" : "平台未应用该设置，请查看能力诊断";
        });
    }

    private async Task RunSafelyAsync(Func<Task> action)
    {
        if (_disposed || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or AiAssistantDomainException)
        {
            StatusMessage = "操作未完成，请检查平台权限、剩余磁盘空间或查看诊断页";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnMetricsUpdated(object? sender, CapturePipelineMetrics metrics)
    {
        if (_disposed)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyMetrics(metrics);
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                {
                    ApplyMetrics(metrics);
                }
            });
        }
    }

    private void ApplyMetrics(CapturePipelineMetrics metrics)
    {
        IsPaused = metrics.IsPaused;
        MetricsText = $"队列 {metrics.QueuedDataItems} · 已保存 {metrics.PersistedEvents} · 合并重复 {metrics.CoalescedDuplicateFrames} · 黑名单拦截 {metrics.DroppedByBlacklist} · 过载丢弃 {metrics.DroppedByOverload}";
    }
}
