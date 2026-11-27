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
public sealed class DataManagementViewModel : ObservableObject, IDisposable
{
    private readonly IDataDeletionService _deletion;
    private readonly IClock _clock;
    private readonly IDatabaseMaintenanceService? _maintenance;
    private readonly IRetentionService? _retention;
    private readonly RetentionSettingsUseCase? _retentionSettings;
    private readonly ProgressPulseController _progressPulse;
    private DateTimeOffset? _selectedDate;
    private PendingAction _pending;
    private string _deleteStatusMessage = "删除不可撤销；加密媒体与结构化事件会同时移除。";
    private string _deleteErrorMessage = string.Empty;
    private string _backupStatusMessage = string.Empty;
    private string _backupErrorMessage = string.Empty;
    private bool _isDeleting;
    private bool _isBackingUp;
    private string? _pendingRestorePath;
    private string _pendingRestoreName = string.Empty;
    private bool _restartAvailable;
    private string? _pendingBackupPath;
    private string _pendingBackupKey = string.Empty;
    private bool _isAwaitingBackupKey;
    private bool _backupKeyConfirmed;
    private bool _backupExportCompleted;
    private string _restoreKeyInput = string.Empty;
    private bool _isAwaitingRestoreKey;
    private RetentionSettings _effectiveRetention = RetentionSettings.Default;
    private int _mediaRetentionDays = RetentionSettings.Default.MediaRetentionDays;
    private int _commandAudioRetentionDays = RetentionSettings.Default.CommandAudioRetentionDays;
    private int _maxStorageGigabytes = RetentionSettings.Default.MaxStorageGigabytes;
    private string _retentionStatusMessage = string.Empty;
    private string _retentionErrorMessage = string.Empty;
    private bool _isRetentionBusy;
    private double _progressPulseValue;

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
        _progressPulse = new ProgressPulseController(value => ProgressPulse = value);
        _selectedDate = _clock.LocalNow.Date;
        DeleteDateCommand = new AsyncRelayCommand(DeleteDateAsync);
        DeleteAllCommand = new AsyncRelayCommand(DeleteAllAsync);
        CancelPendingCommand = new AsyncRelayCommand(CancelPendingAsync);
        BackupCommand = new AsyncRelayCommand(BackupAsync);
        ConfirmBackupCommand = new AsyncRelayCommand(ConfirmBackupAsync);
        CancelBackupKeyCommand = new RelayCommand(CancelBackupKey);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync);
        ValidateRestoreKeyCommand = new AsyncRelayCommand(ValidateRestoreKeyAsync);
        CancelRestoreKeyCommand = new RelayCommand(CancelRestoreKey);
        CancelStagedRestoreCommand = new AsyncRelayCommand(CancelStagedRestoreAsync);
        RestartCommand = new RelayCommand(RequestRestart);
        SaveRetentionCommand = new AsyncRelayCommand(SaveRetentionAsync);
        RunRetentionSweepCommand = new AsyncRelayCommand(RunRetentionSweepAsync);
        LoadRetentionSettings();
    }

    public IAsyncRelayCommand DeleteDateCommand { get; }

    public IAsyncRelayCommand DeleteAllCommand { get; }

    public IAsyncRelayCommand CancelPendingCommand { get; }

    public IAsyncRelayCommand BackupCommand { get; }

    public IAsyncRelayCommand ConfirmBackupCommand { get; }

    public IRelayCommand CancelBackupKeyCommand { get; }

    public IAsyncRelayCommand RestoreCommand { get; }

    public IAsyncRelayCommand ValidateRestoreKeyCommand { get; }

    public IRelayCommand CancelRestoreKeyCommand { get; }

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

    /// <summary>Progress text for the delete card only; backup and retention report in their own cards.</summary>
    public string DeleteStatusMessage
    {
        get => _deleteStatusMessage;
        private set => SetProperty(ref _deleteStatusMessage, value);
    }

    public string DeleteErrorMessage
    {
        get => _deleteErrorMessage;
        private set
        {
            if (SetProperty(ref _deleteErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasDeleteError));
            }
        }
    }

    public bool HasDeleteError => !string.IsNullOrEmpty(DeleteErrorMessage);

    public string BackupStatusMessage
    {
        get => _backupStatusMessage;
        private set => SetProperty(ref _backupStatusMessage, value);
    }

    public string BackupErrorMessage
    {
        get => _backupErrorMessage;
        private set
        {
            if (SetProperty(ref _backupErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasBackupError));
            }
        }
    }

    public bool HasBackupError => !string.IsNullOrEmpty(BackupErrorMessage);

    /// <summary>True while the freshly generated backup key is shown and waiting for the user.</summary>
    public bool IsAwaitingBackupKey
    {
        get => _isAwaitingBackupKey;
        private set
        {
            if (SetProperty(ref _isAwaitingBackupKey, value))
            {
                OnPropertyChanged(nameof(CanConfirmBackup));
                OnPropertyChanged(nameof(CanStartBackup));
                OnPropertyChanged(nameof(CanStartRestore));
            }
        }
    }

    public string PendingBackupKey
    {
        get => _pendingBackupKey;
        private set => SetProperty(ref _pendingBackupKey, value);
    }

    public bool BackupKeyConfirmed
    {
        get => _backupKeyConfirmed;
        set
        {
            if (SetProperty(ref _backupKeyConfirmed, value))
            {
                OnPropertyChanged(nameof(CanConfirmBackup));
            }
        }
    }

    public bool BackupExportCompleted
    {
        get => _backupExportCompleted;
        private set
        {
            if (SetProperty(ref _backupExportCompleted, value))
            {
                OnPropertyChanged(nameof(BackupKeyActionText));
                OnPropertyChanged(nameof(CanConfirmBackup));
            }
        }
    }

    public bool CanConfirmBackup => BackupExportCompleted || (BackupKeyConfirmed && !IsBusy);

    public string BackupKeyActionText => BackupExportCompleted ? "完成" : "开始导出";

    /// <summary>True while the restore card asks for the backup key of the selected archive.</summary>
    public bool IsAwaitingRestoreKey
    {
        get => _isAwaitingRestoreKey;
        private set
        {
            if (SetProperty(ref _isAwaitingRestoreKey, value))
            {
                OnPropertyChanged(nameof(CanValidateRestoreKey));
                OnPropertyChanged(nameof(CanStartBackup));
                OnPropertyChanged(nameof(CanStartRestore));
            }
        }
    }

    public string RestoreKeyInput
    {
        get => _restoreKeyInput;
        set
        {
            if (SetProperty(ref _restoreKeyInput, value))
            {
                OnPropertyChanged(nameof(CanValidateRestoreKey));
            }
        }
    }

    public bool CanValidateRestoreKey =>
        IsAwaitingRestoreKey && !IsBusy && !string.IsNullOrWhiteSpace(RestoreKeyInput);

    public bool CanStartBackup => !IsAwaitingBackupKey && !IsAwaitingRestoreKey && !IsBusy;

    public bool CanStartRestore => !IsAwaitingBackupKey && !IsAwaitingRestoreKey && !IsBusy;

    /// <summary>Only one section may run at a time; each section still owns its own progress bar.</summary>
    public bool IsBusy => IsDeleting || IsBackingUp || IsRetentionBusy;

    /// <summary>
    /// Determinate 0..100 progress driven while busy, so every bar keeps advancing across tab
    /// switches. All three cards share it because only one section can run at a time.
    /// </summary>
    public double ProgressPulse
    {
        get => _progressPulseValue;
        private set => SetProperty(ref _progressPulseValue, value);
    }

    public bool IsDeleting
    {
        get => _isDeleting;
        private set
        {
            if (SetProperty(ref _isDeleting, value))
            {
                NotifyBusyStateChanged();
            }
        }
    }

    public bool IsBackingUp
    {
        get => _isBackingUp;
        private set
        {
            if (SetProperty(ref _isBackingUp, value))
            {
                NotifyBusyStateChanged();
            }
        }
    }

    public bool IsRetentionBusy
    {
        get => _isRetentionBusy;
        private set
        {
            if (SetProperty(ref _isRetentionBusy, value))
            {
                NotifyBusyStateChanged();
            }
        }
    }

    private void NotifyBusyStateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanConfirmBackup));
        OnPropertyChanged(nameof(CanValidateRestoreKey));
        OnPropertyChanged(nameof(CanStartBackup));
        OnPropertyChanged(nameof(CanStartRestore));
        _progressPulse.SetBusy(IsBusy);
    }

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
            $"将用「{_pendingRestoreName}」覆盖当前数据库与加密媒体，并为当前机器重新绑定主密钥，重启后生效；当前数据无法恢复。再次点击“确认恢复”继续。"),
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

    public string RetentionErrorMessage
    {
        get => _retentionErrorMessage;
        private set
        {
            if (SetProperty(ref _retentionErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasRetentionError));
            }
        }
    }

    public bool HasRetentionError => !string.IsNullOrEmpty(RetentionErrorMessage);

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

    /// <summary>
    /// Deliberately free of concrete windows: the numbers live only in the retention card, so this
    /// card cannot go stale after the user edits them.
    /// </summary>
    public string RetentionNotice { get; } =
        "结构化事件长期保留；加密媒体按「保留与清理」中的保留期自动安全擦除，超出存储上限时从最旧的媒体开始擦除。" +
        "删除后原始媒体无法恢复，问答与周报只会引用仍存在的结构化证据。";

    public string ExportNotice { get; } =
        "导出入口：日报、周报、问答与诊断均支持 Markdown / SVG / PNG / PDF / JSON；" +
        "数据备份为可跨机器迁移的独立加密 ZIP（数据库 + 媒体 + 主密钥），导出时生成备份密钥，可在本页导出或恢复。所有导出都在本机生成。";

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
    }

    private async Task SaveRetentionAsync()
    {
        if (_retentionSettings is null || IsBusy)
        {
            return;
        }

        IsRetentionBusy = true;
        RetentionErrorMessage = string.Empty;
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
            RetentionErrorMessage = "保存保留设置失败，请检查数据目录权限。";
        }
        finally
        {
            IsRetentionBusy = false;
        }
    }

    private async Task RunRetentionSweepAsync()
    {
        if (_retention is null || IsBusy)
        {
            return;
        }

        IsRetentionBusy = true;
        RetentionErrorMessage = string.Empty;
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
            RetentionErrorMessage = "清理未完成，请检查数据库状态后重试。";
        }
        finally
        {
            IsRetentionBusy = false;
        }
    }

    private async Task BackupAsync()
    {
        if (_maintenance is null || IsBusy || IsAwaitingBackupKey)
        {
            return;
        }

        if (SaveFilePicker is null)
        {
            BackupErrorMessage = "当前环境无法打开保存对话框。";
            return;
        }

        BackupErrorMessage = string.Empty;
        var picked = await SaveFilePicker.PickSaveFileAsync(
            $"AiAssistant-加密备份-{_clock.LocalNow:yyyyMMdd-HHmmss}.zip",
            [new SaveFileType("加密备份", "zip")]);
        if (picked is null)
        {
            BackupStatusMessage = "已取消备份。";
            return;
        }

        _pendingBackupPath = picked.Path;
        PendingBackupKey = _maintenance.CreateBackupKey();
        BackupKeyConfirmed = false;
        BackupExportCompleted = false;
        IsAwaitingBackupKey = true;
        BackupStatusMessage = "已生成备份密钥。请先保存密钥，再开始导出。";
    }

    private async Task ConfirmBackupAsync()
    {
        if (BackupExportCompleted)
        {
            FinishBackupKey();
            return;
        }

        if (_maintenance is null ||
            IsBusy ||
            !BackupKeyConfirmed ||
            _pendingBackupPath is null ||
            string.IsNullOrWhiteSpace(PendingBackupKey))
        {
            return;
        }

        IsBackingUp = true;
        BackupErrorMessage = string.Empty;
        try
        {
            await _maintenance.BackupEncryptedAsync(_pendingBackupPath, PendingBackupKey, CancellationToken.None);
            BackupExportCompleted = true;
            BackupStatusMessage = $"已导出加密备份：{_pendingBackupPath}。请确认密钥已另行保存。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Core.Exceptions.AiAssistantDomainException)
        {
            BackupErrorMessage = "备份失败，请检查目标目录权限与剩余磁盘空间。";
        }
        finally
        {
            IsBackingUp = false;
        }
    }

    private void CancelBackupKey()
    {
        if (IsBusy)
        {
            return;
        }

        var completed = BackupExportCompleted;
        FinishBackupKey();
        if (!completed)
        {
            BackupErrorMessage = string.Empty;
            BackupStatusMessage = "已取消备份。";
        }
    }

    private void FinishBackupKey()
    {
        _pendingBackupPath = null;
        PendingBackupKey = string.Empty;
        BackupKeyConfirmed = false;
        BackupExportCompleted = false;
        IsAwaitingBackupKey = false;
    }

    private async Task RestoreAsync()
    {
        if (_maintenance is null || IsBusy)
        {
            return;
        }

        if (_pending == PendingAction.Restore)
        {
            IsBackingUp = true;
            BackupErrorMessage = string.Empty;
            try
            {
                if (!await _maintenance.CommitStagedRestoreAsync(CancellationToken.None))
                {
                    BackupErrorMessage = "暂存的恢复已失效，请重新选择备份。";
                    SetPending(PendingAction.None);
                    return;
                }

                RestartAvailable = true;
                SetPending(PendingAction.None);
                BackupStatusMessage = "已确认恢复，重启后将覆盖当前数据。请点击「立即重启」完成恢复。";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Core.Exceptions.AiAssistantDomainException)
            {
                BackupErrorMessage = "确认恢复失败，请检查数据目录权限。";
            }
            finally
            {
                IsBackingUp = false;
            }

            return;
        }

        if (IsAwaitingRestoreKey)
        {
            return;
        }

        if (OpenFilePicker is null)
        {
            BackupErrorMessage = "当前环境无法打开文件选择对话框。";
            return;
        }

        var path = await OpenFilePicker.PickOpenFileAsync(
            "选择加密备份",
            [new SaveFileType("加密备份", "zip")]);
        if (string.IsNullOrWhiteSpace(path))
        {
            BackupStatusMessage = "已取消恢复。";
            return;
        }

        _pendingRestorePath = path;
        _pendingRestoreName = Path.GetFileName(path);
        RestoreKeyInput = string.Empty;
        IsAwaitingRestoreKey = true;
        BackupErrorMessage = string.Empty;
        BackupStatusMessage = "请输入导出时保存的备份密钥。";
    }

    private async Task ValidateRestoreKeyAsync()
    {
        if (_maintenance is null ||
            IsBusy ||
            !IsAwaitingRestoreKey ||
            string.IsNullOrWhiteSpace(RestoreKeyInput) ||
            string.IsNullOrWhiteSpace(_pendingRestorePath))
        {
            return;
        }

        IsBackingUp = true;
        BackupErrorMessage = string.Empty;
        try
        {
            await _maintenance.StageRestoreAsync(_pendingRestorePath, RestoreKeyInput, CancellationToken.None);
            IsAwaitingRestoreKey = false;
            RestoreKeyInput = string.Empty;
            SetPending(PendingAction.Restore);
            BackupStatusMessage = "备份已解密并校验；确认后将覆盖当前数据，重启生效。";
        }
        catch (Core.Exceptions.StorageAuthenticationException)
        {
            BackupErrorMessage = "备份密钥不正确，或备份文件已损坏。请检查后重试。";
        }
        catch (Core.Exceptions.StorageIntegrityException)
        {
            BackupErrorMessage = "备份文件不完整或已损坏，无法恢复。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Core.Exceptions.AiAssistantDomainException)
        {
            BackupErrorMessage = $"恢复未生效：{ex.Message}";
        }
        finally
        {
            IsBackingUp = false;
        }
    }

    private void CancelRestoreKey()
    {
        if (IsBusy)
        {
            return;
        }

        _pendingRestorePath = null;
        _pendingRestoreName = string.Empty;
        RestoreKeyInput = string.Empty;
        IsAwaitingRestoreKey = false;
        BackupErrorMessage = string.Empty;
        BackupStatusMessage = "已取消恢复。";
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

        IsBackingUp = true;
        BackupErrorMessage = string.Empty;
        try
        {
            var discarded = await _maintenance.DiscardStagedRestoreAsync(CancellationToken.None);
            if (discarded)
            {
                RestartAvailable = false;
            }

            BackupStatusMessage = discarded
                ? "已取消暂存的恢复，当前数据保持不变。"
                : "没有待恢复的备份。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Core.Exceptions.AiAssistantDomainException)
        {
            BackupErrorMessage = "取消恢复失败，请检查数据目录权限。";
        }
        finally
        {
            IsBackingUp = false;
        }
    }

    private async Task DeleteDateAsync()
    {
        // The UI disables both delete buttons while another section runs; the guard keeps
        // automation and direct command calls from starting a purge next to a backup/restore.
        if (IsBusy)
        {
            return;
        }

        if (_pending != PendingAction.DeleteDate)
        {
            SetPending(PendingAction.DeleteDate);
            return;
        }

        await RunAsync(async ct =>
        {
            var result = await _deletion.PurgeDateAsync(ResolveDate(), ct);
            DeleteStatusMessage = string.Create(
                CultureInfo.InvariantCulture,
                $"已删除 {ResolveDate():yyyy-MM-dd}：事件 {result.EventsDeleted} 条、日报 {result.ReportsDeleted} 份、任务 {result.JobsDeleted} 个。");
        });
    }

    private async Task DeleteAllAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (_pending != PendingAction.DeleteAll)
        {
            SetPending(PendingAction.DeleteAll);
            return;
        }

        await RunAsync(async ct =>
        {
            var result = await _deletion.PurgeAllAsync(ct);
            DeleteStatusMessage = string.Create(
                CultureInfo.InvariantCulture,
                $"已清空本地数据：事件 {result.EventsDeleted} 条、媒体 {result.MediaFilesDeleted} 个、日报 {result.ReportsDeleted} 份、语音日志 {result.VoiceCommandsDeleted} 条。");
        });
    }

    private async Task CancelPendingAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var cancelled = _pending;
        _pendingRestorePath = null;
        _pendingRestoreName = string.Empty;
        SetPending(PendingAction.None);

        // The cancel button lives in both the delete and the backup card; report back to the one
        // whose confirmation was actually withdrawn.
        if (cancelled == PendingAction.Restore)
        {
            if (_maintenance is not null)
            {
                try
                {
                    await _maintenance.DiscardStagedRestoreAsync(CancellationToken.None);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // Best effort: the next start also drops an uncommitted staging directory.
                }
            }

            BackupStatusMessage = "已取消当前操作。";
        }
        else
        {
            DeleteStatusMessage = "已取消当前操作。";
        }
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
        IsDeleting = true;
        DeleteErrorMessage = string.Empty;
        try
        {
            await action(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Core.Exceptions.AiAssistantDomainException)
        {
            DeleteErrorMessage = "删除未完成。数据库可能被占用，请关闭其他实例后重试。";
            DeleteStatusMessage = "删除失败";
        }
        finally
        {
            SetPending(PendingAction.None);
            IsDeleting = false;
        }
    }

    public void Dispose() => _progressPulse.Dispose();
}
