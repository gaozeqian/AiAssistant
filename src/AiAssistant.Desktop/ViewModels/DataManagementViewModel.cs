using System.Globalization;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiAssistant.Desktop.ViewModels;

internal enum PendingAction
{
    None = 0,
    DeleteDate = 1,
    DeleteAll = 2,
    Restore = 3
}

/// <summary>
/// Local data administration: two-step confirmed deletion, encrypted backup export, staged restore,
/// and the retention policy that decides when media is erased. Export entry points for reports,
/// search and diagnostics live in their own panels.
/// </summary>
public sealed class DataManagementViewModel : ObservableObject
{
    private readonly IDataDeletionService _deletion;
    private readonly IClock _clock;
    private readonly IDatabaseMaintenanceService? _maintenance;
    private readonly IRetentionService? _retention;
    private readonly RetentionSettingsUseCase? _retentionSettings;
    private DateTimeOffset? _selectedDate;
    private PendingAction _pending;
    private string _statusMessage = "删除不可撤销；加密媒体与结构化事件会同时移除。";
    private string _errorMessage = string.Empty;
    private bool _isBusy;
    private string? _pendingRestorePath;
    private string _pendingRestoreName = string.Empty;
    private bool _restartAvailable;
    private RetentionSettings _effectiveRetention = RetentionSettings.Default;
    private int _mediaRetentionDays = RetentionSettings.Default.MediaRetentionDays;
    private int _commandAudioRetentionDays = RetentionSettings.Default.CommandAudioRetentionDays;
    private int _maxStorageGigabytes = RetentionSettings.Default.MaxStorageGigabytes;
    private string _retentionStatusMessage = string.Empty;

    public DataManagementViewModel(
        IDataDeletionService deletion,
        IClock? clock = null,
        IDatabaseMaintenanceService? maintenance = null,
        IRetentionService? retention = null,
        RetentionSettingsUseCase? retentionSettings = null)
    {
        _deletion = deletion ?? throw new ArgumentNullException(nameof(deletion));
        _clock = clock ?? new SystemClock();
        _maintenance = maintenance;
        _retention = retention;
        _retentionSettings = retentionSettings;
        _selectedDate = _clock.LocalNow.Date;
        DeleteDateCommand = new AsyncRelayCommand(DeleteDateAsync);
        DeleteAllCommand = new AsyncRelayCommand(DeleteAllAsync);
        CancelPendingCommand = new RelayCommand(CancelPending);
        BackupCommand = new AsyncRelayCommand(BackupAsync);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync);
        CancelStagedRestoreCommand = new AsyncRelayCommand(CancelStagedRestoreAsync);
        RestartCommand = new RelayCommand(RequestRestart);
        SaveRetentionCommand = new AsyncRelayCommand(SaveRetentionAsync);
        RunRetentionSweepCommand = new AsyncRelayCommand(RunRetentionSweepAsync);
        LoadRetentionSettings();
    }

    public IAsyncRelayCommand DeleteDateCommand { get; }

    public IAsyncRelayCommand DeleteAllCommand { get; }

    public IRelayCommand CancelPendingCommand { get; }

    public IAsyncRelayCommand BackupCommand { get; }

    public IAsyncRelayCommand RestoreCommand { get; }

    public IAsyncRelayCommand CancelStagedRestoreCommand { get; }

    public IRelayCommand RestartCommand { get; }

    public IAsyncRelayCommand SaveRetentionCommand { get; }

    public IAsyncRelayCommand RunRetentionSweepCommand { get; }

    /// <summary>Raised after a restore is staged so the shell can restart into the new data set.</summary>
    public event EventHandler? RestartRequested;

    /// <summary>Save-file dialog, wired by the shell after the window exists.</summary>
    public ISaveFilePicker? SaveFilePicker { get; set; }

    /// <summary>Open-file dialog used to pick the backup archive for restore.</summary>
    public IOpenFilePicker? OpenFilePicker { get; set; }

    public bool HasBackupSupport => _maintenance is not null;

    public bool HasRetentionSettings => _retentionSettings is not null;

    public bool HasRetentionSweep => _retention is not null;

    public DateTimeOffset? SelectedDate
    {
        get => _selectedDate;
        set => SetProperty(ref _selectedDate, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
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

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasPending => _pending != PendingAction.None;

    public bool HasPendingDeletion => _pending is PendingAction.DeleteDate or PendingAction.DeleteAll;

    public bool HasPendingRestore => _pending == PendingAction.Restore;

    public string PendingText => _pending switch
    {
        PendingAction.DeleteDate => string.Create(
            CultureInfo.InvariantCulture,
            $"将永久删除 {ResolveDate():yyyy-MM-dd} 的全部事件、加密媒体、日报与排队任务。再次点击“确认删除”继续。"),
        PendingAction.DeleteAll => "将永久删除本机全部事件、媒体、日报、语音文本日志与向量索引，且无法恢复。再次点击“确认清空”继续。",
        PendingAction.Restore => string.Create(
            CultureInfo.InvariantCulture,
            $"将用「{_pendingRestoreName}」覆盖当前数据库、密钥封装与加密媒体，重启后生效；当前数据无法恢复。再次点击“确认恢复”继续。"),
        _ => string.Empty
    };

    public string DeleteDateButtonText => _pending == PendingAction.DeleteDate ? "确认删除" : "删除该日数据";

    public string DeleteAllButtonText => _pending == PendingAction.DeleteAll ? "确认清空" : "清空全部本地数据";

    public string RestoreButtonText => _pending == PendingAction.Restore ? "确认恢复" : "从备份恢复…";

    /// <summary>True once a restore has been validated and is waiting for the next start.</summary>
    public bool RestartAvailable
    {
        get => _restartAvailable;
        private set => SetProperty(ref _restartAvailable, value);
    }

    public int MediaRetentionDays
    {
        get => _mediaRetentionDays;
        set => SetProperty(ref _mediaRetentionDays, value);
    }

    public int CommandAudioRetentionDays
    {
        get => _commandAudioRetentionDays;
        set => SetProperty(ref _commandAudioRetentionDays, value);
    }

    public int MaxStorageGigabytes
    {
        get => _maxStorageGigabytes;
        set => SetProperty(ref _maxStorageGigabytes, value);
    }

    public string RetentionStatusMessage
    {
        get => _retentionStatusMessage;
        private set => SetProperty(ref _retentionStatusMessage, value);
    }

    /// <summary>Effective policy, i.e. what is actually applied to writes and sweeps.</summary>
    public string RetentionPolicyText
    {
        get
        {
            var cap = _effectiveRetention.MaxStorageGigabytes <= 0
                ? "不限"
                : $"{_effectiveRetention.MaxStorageGigabytes} GB";
            return string.Create(
                CultureInfo.InvariantCulture,
                $"媒体保留 {_effectiveRetention.MediaRetentionDays} 天 · 命令音频保留 {_effectiveRetention.CommandAudioRetentionDays} 天 · 存储上限 {cap}");
        }
    }

    public string RetentionNotice
    {
        get
        {
            var capText = _effectiveRetention.MaxStorageGigabytes <= 0
                ? "当前未设置存储上限"
                : $"占用超过 {_effectiveRetention.MaxStorageGigabytes} GB 时从最旧的媒体开始擦除";
            return string.Create(
                CultureInfo.InvariantCulture,
                $"结构化事件长期保留；加密媒体按写入时的 {_effectiveRetention.MediaRetentionDays} 天保留期自动安全擦除，{capText}。删除后原始媒体无法恢复，问答与周报只会引用仍存在的结构化证据。");
        }
    }

    public string ExportNotice { get; } =
        "导出入口：日报、周报、问答与诊断均支持 Markdown / SVG / PNG / PDF / JSON；" +
        "数据备份为独立加密 ZIP（数据库 + 密钥封装 + 加密媒体），可在本页导出或恢复。所有导出都在本机生成。";

    private void LoadRetentionSettings()
    {
        if (_retentionSettings is null)
        {
            return;
        }

        try
        {
            _effectiveRetention = _retentionSettings.Get();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _effectiveRetention = RetentionSettings.Default;
            RetentionStatusMessage = "读取保留设置失败，已使用默认值。";
        }

        MediaRetentionDays = _effectiveRetention.MediaRetentionDays;
        CommandAudioRetentionDays = _effectiveRetention.CommandAudioRetentionDays;
        MaxStorageGigabytes = _effectiveRetention.MaxStorageGigabytes;
        OnPropertyChanged(nameof(RetentionPolicyText));
        OnPropertyChanged(nameof(RetentionNotice));
    }

    private async Task SaveRetentionAsync()
    {
        if (_retentionSettings is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            _effectiveRetention = await _retentionSettings.SaveAsync(
                MediaRetentionDays,
                CommandAudioRetentionDays,
                MaxStorageGigabytes,
                CancellationToken.None);
            MediaRetentionDays = _effectiveRetention.MediaRetentionDays;
            CommandAudioRetentionDays = _effectiveRetention.CommandAudioRetentionDays;
            MaxStorageGigabytes = _effectiveRetention.MaxStorageGigabytes;
            OnPropertyChanged(nameof(RetentionPolicyText));
            OnPropertyChanged(nameof(RetentionNotice));

            // A lowered cap must take effect now: the write gate rejects new blobs as soon as the
            // directory is over the limit, so waiting for the hourly sweep would break capture.
            if (_retention is not null)
            {
                var purged = await _retention.RunSweepNowAsync(CancellationToken.None);
                RetentionStatusMessage = purged == 0
                    ? "已保存；当前没有需要擦除的媒体。"
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"已保存；已按新策略擦除 {purged} 个过期或超限媒体。");
            }
            else
            {
                RetentionStatusMessage = "已保存；新写入的媒体立即使用新的保留期。";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Core.Exceptions.AiAssistantDomainException)
        {
            ErrorMessage = "保存保留设置失败，请检查数据目录权限。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunRetentionSweepAsync()
    {
        if (_retention is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var purged = await _retention.RunSweepNowAsync(CancellationToken.None);
            RetentionStatusMessage = purged == 0
                ? "清理完成：没有需要擦除的媒体。"
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"清理完成：已安全擦除 {purged} 个过期或超限媒体。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Core.Exceptions.AiAssistantDomainException)
        {
            ErrorMessage = "清理未完成，请检查数据库状态后重试。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task BackupAsync()
    {
        if (_maintenance is null || IsBusy)
        {
            return;
        }

        if (SaveFilePicker is null)
        {
            ErrorMessage = "当前环境无法打开保存对话框。";
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var picked = await SaveFilePicker.PickSaveFileAsync(
                $"AiAssistant-加密备份-{_clock.LocalNow:yyyyMMdd-HHmmss}.zip",
                [new SaveFileType("加密备份", "zip")]);
            if (picked is null)
            {
                StatusMessage = "已取消备份。";
                return;
            }

            await _maintenance.BackupEncryptedAsync(picked.Path, CancellationToken.None);
            StatusMessage = $"已导出加密备份：{picked.Path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Core.Exceptions.AiAssistantDomainException)
        {
            ErrorMessage = "备份失败，请检查目标目录权限与剩余磁盘空间。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreAsync()
    {
        if (_maintenance is null || IsBusy)
        {
            return;
        }

        if (_pending != PendingAction.Restore)
        {
            if (OpenFilePicker is null)
            {
                ErrorMessage = "当前环境无法打开文件选择对话框。";
                return;
            }

            var path = await OpenFilePicker.PickOpenFileAsync(
                "选择加密备份",
                [new SaveFileType("加密备份", "zip"), new SaveFileType("旧版数据库", "db")]);
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusMessage = "已取消恢复。";
                return;
            }

            _pendingRestorePath = path;
            _pendingRestoreName = Path.GetFileName(path);
            SetPending(PendingAction.Restore);
            return;
        }

        if (string.IsNullOrWhiteSpace(_pendingRestorePath))
        {
            SetPending(PendingAction.None);
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await _maintenance.StageRestoreAsync(_pendingRestorePath, CancellationToken.None);
            RestartAvailable = true;
            StatusMessage = "备份已校验，将在重启后覆盖当前数据。请点击「立即重启」完成恢复。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Core.Exceptions.AiAssistantDomainException)
        {
            ErrorMessage = $"恢复未生效：{ex.Message}";
            StatusMessage = "恢复已取消，当前数据未被修改。";
        }
        finally
        {
            _pendingRestorePath = null;
            _pendingRestoreName = string.Empty;
            SetPending(PendingAction.None);
            IsBusy = false;
        }
    }

    private void RequestRestart() => RestartRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Drops the staged archive. Without this a user who changed their mind could not stop the
    /// restore from silently replacing data on the next start.
    /// </summary>
    private async Task CancelStagedRestoreAsync()
    {
        if (_maintenance is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var discarded = await _maintenance.DiscardStagedRestoreAsync(CancellationToken.None);
            if (discarded)
            {
                RestartAvailable = false;
            }

            StatusMessage = discarded
                ? "已取消暂存的恢复，当前数据保持不变。"
                : "没有待恢复的备份。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Core.Exceptions.AiAssistantDomainException)
        {
            ErrorMessage = "取消恢复失败，请检查数据目录权限。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteDateAsync()
    {
        if (_pending != PendingAction.DeleteDate)
        {
            SetPending(PendingAction.DeleteDate);
            return;
        }

        await RunAsync(async ct =>
        {
            var result = await _deletion.PurgeDateAsync(ResolveDate(), ct);
            StatusMessage = string.Create(
                CultureInfo.InvariantCulture,
                $"已删除 {ResolveDate():yyyy-MM-dd}：事件 {result.EventsDeleted} 条、日报 {result.ReportsDeleted} 份、任务 {result.JobsDeleted} 个。");
        });
    }

    private async Task DeleteAllAsync()
    {
        if (_pending != PendingAction.DeleteAll)
        {
            SetPending(PendingAction.DeleteAll);
            return;
        }

        await RunAsync(async ct =>
        {
            var result = await _deletion.PurgeAllAsync(ct);
            StatusMessage = string.Create(
                CultureInfo.InvariantCulture,
                $"已清空本地数据：事件 {result.EventsDeleted} 条、媒体 {result.MediaFilesDeleted} 个、日报 {result.ReportsDeleted} 份、语音日志 {result.VoiceCommandsDeleted} 条。");
        });
    }

    private void CancelPending()
    {
        _pendingRestorePath = null;
        _pendingRestoreName = string.Empty;
        SetPending(PendingAction.None);
        StatusMessage = "已取消当前操作。";
    }

    private void SetPending(PendingAction pending)
    {
        _pending = pending;
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(HasPendingDeletion));
        OnPropertyChanged(nameof(HasPendingRestore));
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(DeleteDateButtonText));
        OnPropertyChanged(nameof(DeleteAllButtonText));
        OnPropertyChanged(nameof(RestoreButtonText));
    }

    private DateOnly ResolveDate() =>
        DateOnly.FromDateTime((SelectedDate ?? _clock.LocalNow).LocalDateTime);

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await action(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Core.Exceptions.AiAssistantDomainException)
        {
            ErrorMessage = "删除未完成。数据库可能被占用，请关闭其他实例后重试。";
            StatusMessage = "删除失败";
        }
        finally
        {
            SetPending(PendingAction.None);
            IsBusy = false;
        }
    }
}
