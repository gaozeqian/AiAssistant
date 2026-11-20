using System.Collections.ObjectModel;
using System.Globalization;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiAssistant.Desktop.ViewModels;

public sealed class DiagnosticsViewModel : ObservableObject
{
    private readonly DiagnosticsUseCase _diagnostics;
    private string _platformName = "正在检测平台能力…";
    private string _databaseText = "尚未检查";
    private string _pipelineText = string.Empty;
    private string _retentionText = "尚未执行清理扫描";
    private string _exportPreviewJson = string.Empty;
    private string _lastRefreshText = "尚未刷新";
    private string _errorMessage = string.Empty;
    private bool _isLoading;

    public DiagnosticsViewModel(DiagnosticsUseCase diagnostics)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        GeneratePreviewCommand = new AsyncRelayCommand(GenerateExportPreviewAsync);
    }

    public ObservableCollection<CapabilityItem> Capabilities { get; } = [];

    public ObservableCollection<ReportJobItem> RecentJobs { get; } = [];

    public ObservableCollection<DiagnosticLogItem> RecentLogs { get; } = [];

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand GeneratePreviewCommand { get; }

    public string PlatformName
    {
        get => _platformName;
        private set => SetProperty(ref _platformName, value);
    }

    public string DatabaseText
    {
        get => _databaseText;
        private set => SetProperty(ref _databaseText, value);
    }

    public string PipelineText
    {
        get => _pipelineText;
        private set => SetProperty(ref _pipelineText, value);
    }

    public string RetentionText
    {
        get => _retentionText;
        private set => SetProperty(ref _retentionText, value);
    }

    public string ExportPreviewJson
    {
        get => _exportPreviewJson;
        private set
        {
            if (SetProperty(ref _exportPreviewJson, value))
            {
                OnPropertyChanged(nameof(HasExportPreview));
            }
        }
    }

    public bool HasExportPreview => !string.IsNullOrEmpty(ExportPreviewJson);

    public string LastRefreshText
    {
        get => _lastRefreshText;
        private set => SetProperty(ref _lastRefreshText, value);
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

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public async Task RefreshAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var snapshot = await _diagnostics.GetSnapshotAsync();
            PlatformName = snapshot.PlatformName;
            DatabaseText = snapshot.DatabaseHealth.IsHealthy
                ? $"正常 · Schema v{snapshot.DatabaseHealth.SchemaVersion.Number} · {snapshot.DatabaseHealth.EventCount} 条事件 · {snapshot.DatabaseHealth.EncryptedBlobCount} 个加密媒体"
                : "数据库健康检查未通过";
            PipelineText = $"队列 {snapshot.PipelineMetrics.QueuedDataItems} / 已保存 {snapshot.PipelineMetrics.PersistedEvents} / 控制命令 {snapshot.PipelineMetrics.ControlCommandsProcessed}";
            RetentionText = snapshot.RetentionMetrics is null
                ? "默认保留策略已配置"
                : $"已扫描 {snapshot.RetentionMetrics.TotalSweeps} 次 · 安全擦除过期媒体 {snapshot.RetentionMetrics.TotalPurgedBlobs} 个";

            Capabilities.Clear();
            foreach (var capability in snapshot.Capabilities)
            {
                Capabilities.Add(new CapabilityItem(capability));
            }

            RecentJobs.Clear();
            foreach (var job in snapshot.RecentJobs)
            {
                RecentJobs.Add(new ReportJobItem(job));
            }

            RecentLogs.Clear();
            if (snapshot.RecentLogs is not null)
            {
                foreach (var log in snapshot.RecentLogs)
                {
                    RecentLogs.Add(new DiagnosticLogItem(log));
                }
            }

            LastRefreshText = $"最后刷新：{DateTimeOffset.Now:HH:mm:ss} · 诊断不包含正文、密钥或完整文件路径";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Core.Exceptions.AiAssistantDomainException)
        {
            ErrorMessage = "诊断读取未完成。数据库或平台能力可能不可用，请检查权限与剩余磁盘空间。";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task GenerateExportPreviewAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            ExportPreviewJson = await _diagnostics.BuildSanitizedExportPreviewAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Core.Exceptions.AiAssistantDomainException)
        {
            ErrorMessage = "无法生成诊断预览，请检查存储状态。";
        }
        finally
        {
            IsLoading = false;
        }
    }
}

public sealed class CapabilityItem(CapabilityState capability)
{
    public string KindText => capability.Kind switch
    {
        CapabilityKind.ScreenCapture => "屏幕采集",
        CapabilityKind.WindowActivity => "窗口活动",
        CapabilityKind.FileActivity => "文件活动",
        CapabilityKind.AudioCapture => "音频采集",
        CapabilityKind.AccessibilityText => "无障碍文本",
        CapabilityKind.Ocr => "OCR",
        CapabilityKind.GlobalHotkey => "全局热键",
        CapabilityKind.SystemTray => "系统托盘",
        CapabilityKind.AutoStart => "开机自启",
        CapabilityKind.KeyStore => "系统密钥库",
        CapabilityKind.VectorSearch => "向量检索",
        CapabilityKind.WakeWord => "唤醒词",
        _ => capability.Kind.ToString()
    };

    public string StatusText => capability.Status switch
    {
        CapabilityStatus.Supported => "可用",
        CapabilityStatus.PermissionRequired => "需要权限",
        CapabilityStatus.Degraded => "降级",
        CapabilityStatus.Unavailable => "不可用",
        CapabilityStatus.DisabledByPolicy => "策略禁用",
        _ => "未知"
    };

    public string Summary => capability.Summary;

    public string RemediationHint => capability.RemediationHint ?? "无需额外操作";

    public bool NeedsAttention => capability.Status is not CapabilityStatus.Supported;
}

public sealed class ReportJobItem(ReportJob job)
{
    public string DateText => job.TargetDate.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);

    public string StatusText => job.Status switch
    {
        ReportJobStatus.Pending => "等待执行",
        ReportJobStatus.Running => "执行中",
        ReportJobStatus.Completed => "已完成",
        ReportJobStatus.SkippedAlreadyExists => "已跳过",
        ReportJobStatus.Failed => "失败",
        _ => "未知"
    };

    public string AttemptText => $"第 {job.Attempts} 次";

    public string CreatedText => job.CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
}

public sealed class DiagnosticLogItem(DiagnosticLogEntry entry)
{
    public string TimeText => entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public string LevelText => entry.Level;
    public string CategoryText => entry.Category;
    public string MessageText => entry.SanitizedMessage;
}
