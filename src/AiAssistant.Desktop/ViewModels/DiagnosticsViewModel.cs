using System.Collections.ObjectModel;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiAssistant.Desktop.ViewModels;

/// <summary>
/// Local diagnostics surface. Model configuration and secret administration live in their own
/// view models; this type owns snapshot formatting, MCP status and sanitized export.
/// </summary>
public sealed class DiagnosticsViewModel : ObservableObject
{
    /// <summary>Diagnostics is an overview, not a job history browser; keep the list scannable.</summary>
    public const int RecentJobDisplayLimit = 5;

    private readonly DiagnosticsUseCase _diagnostics;
    private readonly IClock _clock;
    private readonly ILocalMcpControl? _mcp;
    private readonly string? _exportDirectory;
    private readonly RetentionSettingsUseCase? _retentionSettings;
    private readonly IRetentionService? _retention;
    private string _platformName = "正在检测平台能力…";
    private string _databaseText = "尚未检查";
    private string _pipelineText = string.Empty;
    private string _retentionText = "尚未执行清理扫描";
    private string _retentionPolicyText = "保留策略未接入当前运行时";
    private string _retentionStatusText = string.Empty;
    private string _modelText = "尚未探测本机模型";
    private string _modelEndpointsText = "尚未探测端点能力";
    private string _exportPreviewJson = string.Empty;
    private string _lastRefreshText = "尚未刷新";
    private string _errorMessage = string.Empty;
    private string _mcpStatusText = "本地 MCP 默认关闭";
    private string _mcpTokenText = "点击“显示令牌”获取本地凭据";
    private bool _mcpEnabled;
    private string _exportStatusText = string.Empty;
    private string _recentJobsSummary = "尚未刷新";
    private bool _isLoading;

    public DiagnosticsViewModel(
        DiagnosticsUseCase diagnostics,
        ILocalMcpControl? mcp = null,
        string? exportDirectory = null,
        IModelConfigurationAdmin? modelConfiguration = null,
        IClock? clock = null,
        RetentionSettingsUseCase? retentionSettings = null,
        IRetentionService? retention = null)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _clock = clock ?? new SystemClock();
        _mcp = mcp;
        _exportDirectory = exportDirectory;
        _retentionSettings = retentionSettings;
        _retention = retention;
        ModelConfig = new ModelConfigurationEditorViewModel(modelConfiguration);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        GeneratePreviewCommand = new AsyncRelayCommand(GenerateExportPreviewAsync);
        ExportPreviewCommand = new AsyncRelayCommand(ExportPreviewAsync);
        RevealMcpTokenCommand = new AsyncRelayCommand(RevealMcpTokenAsync);
        RotateMcpTokenCommand = new AsyncRelayCommand(RotateMcpTokenAsync);
        ToggleMcpEnabledCommand = new AsyncRelayCommand(ToggleMcpEnabledAsync);
        ExecuteCapabilityCommand = new RelayCommand<CapabilityAction?>(ExecuteCapabilityAction);
        RunRetentionSweepCommand = new AsyncRelayCommand(RunRetentionSweepAsync);
        _mcpEnabled = _mcp?.GetSnapshot().Enabled ?? false;
        _retentionPolicyText = FormatRetentionPolicy();
    }

    public ObservableCollection<CapabilityItem> Capabilities { get; } = [];

    public ObservableCollection<ReportJobItem> RecentJobs { get; } = [];

    public string RecentJobsSummary
    {
        get => _recentJobsSummary;
        private set => SetProperty(ref _recentJobsSummary, value);
    }

    public ObservableCollection<DiagnosticLogItem> RecentLogs { get; } = [];

    public ModelConfigurationEditorViewModel ModelConfig { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand GeneratePreviewCommand { get; }

    public IAsyncRelayCommand ExportPreviewCommand { get; }

    public IAsyncRelayCommand RevealMcpTokenCommand { get; }

    public IAsyncRelayCommand RotateMcpTokenCommand { get; }

    public IAsyncRelayCommand ToggleMcpEnabledCommand { get; }

    public IRelayCommand<CapabilityAction?> ExecuteCapabilityCommand { get; }

    public IAsyncRelayCommand RunRetentionSweepCommand { get; }

    public bool HasRetentionSweep => _retention is not null;

    /// <summary>Raised when a capability hint asks the user to continue on the voice page.</summary>
    public event EventHandler? VoicePageRequested;

    public bool HasMcpControl => _mcp is not null;

    /// <summary>Save-file dialog, wired by the shell after the window exists; null falls back to the export directory.</summary>
    public ISaveFilePicker? SaveFilePicker { get; set; }

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

    /// <summary>Effective retention policy, so "过期擦除" always shows the actual window.</summary>
    public string RetentionPolicyText
    {
        get => _retentionPolicyText;
        private set => SetProperty(ref _retentionPolicyText, value);
    }

    public string RetentionStatusText
    {
        get => _retentionStatusText;
        private set
        {
            if (SetProperty(ref _retentionStatusText, value))
            {
                OnPropertyChanged(nameof(HasRetentionStatus));
            }
        }
    }

    public bool HasRetentionStatus => !string.IsNullOrEmpty(RetentionStatusText);

    public string ModelText
    {
        get => _modelText;
        private set => SetProperty(ref _modelText, value);
    }

    public string ModelEndpointsText
    {
        get => _modelEndpointsText;
        private set => SetProperty(ref _modelEndpointsText, value);
    }

    public string ExportPreviewJson
    {
        get => _exportPreviewJson;
        private set
        {
            if (SetProperty(ref _exportPreviewJson, value))
            {
                OnPropertyChanged(nameof(HasExportPreview));
                OnPropertyChanged(nameof(CanExportPreview));
            }
        }
    }

    public bool HasExportPreview => !string.IsNullOrEmpty(ExportPreviewJson);

    /// <summary>Only allow writing a file after the user has actually seen a generated preview.</summary>
    public bool CanExportPreview => HasExportPreview && !IsLoading;

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

    public string McpStatusText
    {
        get => _mcpStatusText;
        private set => SetProperty(ref _mcpStatusText, value);
    }

    public bool McpEnabled
    {
        get => _mcpEnabled;
        private set => SetProperty(ref _mcpEnabled, value);
    }

    public string McpTokenText
    {
        get => _mcpTokenText;
        private set => SetProperty(ref _mcpTokenText, value);
    }

    public string ExportStatusText
    {
        get => _exportStatusText;
        private set
        {
            if (SetProperty(ref _exportStatusText, value))
            {
                OnPropertyChanged(nameof(HasExportStatus));
            }
        }
    }

    public bool HasExportStatus => !string.IsNullOrEmpty(ExportStatusText);

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(CanExportPreview));
            }
        }
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
                ? $"正常 · {snapshot.DatabaseHealth.EventCount} 条事件 · {snapshot.DatabaseHealth.EncryptedBlobCount} 个加密媒体"
                : "数据库健康检查未通过";
            PipelineText = $"队列 {snapshot.PipelineMetrics.QueuedDataItems} / 已保存 {snapshot.PipelineMetrics.PersistedEvents} / 控制命令 {snapshot.PipelineMetrics.ControlCommandsProcessed}";
            RetentionText = snapshot.RetentionMetrics is null
                ? "尚未执行清理扫描"
                : $"已扫描 {snapshot.RetentionMetrics.TotalSweeps} 次 · 安全擦除过期或超限媒体 {snapshot.RetentionMetrics.TotalPurgedBlobs} 个";
            RetentionPolicyText = FormatRetentionPolicy();
            ModelText = snapshot.ModelCapability is null
                ? "未配置模型网关"
                : FormatModelSummary(snapshot.ModelCapability);
            ModelEndpointsText = snapshot.ModelEndpoints is { Count: > 0 }
                ? string.Join(
                    Environment.NewLine,
                    snapshot.ModelEndpoints.Select(DiagnosticsDisplayFormatter.FormatEndpoint))
                : "尚未探测端点能力";

            DiagnosticsDisplayFormatter.Replace(
                Capabilities,
                snapshot.Capabilities.Select(capability => new CapabilityItem(
                    capability,
                    CapabilityActionResolver.Resolve(capability),
                    ExecuteCapabilityCommand)));
            DiagnosticsDisplayFormatter.Replace(
                RecentJobs,
                snapshot.RecentJobs.Take(RecentJobDisplayLimit).Select(job => new ReportJobItem(job)));
            RecentJobsSummary = snapshot.RecentJobs.Count > RecentJobDisplayLimit
                ? $"共 {snapshot.RecentJobs.Count} 条，仅显示最近 {RecentJobDisplayLimit} 条；完整记录见脱敏导出。"
                : $"共 {snapshot.RecentJobs.Count} 条。";
            DiagnosticsDisplayFormatter.Replace(RecentLogs, snapshot.RecentLogs?.Select(log => new DiagnosticLogItem(log)) ?? []);

            if (_mcp is null)
            {
                McpStatusText = "本地 MCP 未接入";
            }
            else
            {
                var mcpSnapshot = _mcp.GetSnapshot();
                McpEnabled = mcpSnapshot.Enabled;
                McpStatusText = DiagnosticsDisplayFormatter.FormatMcpStatus(mcpSnapshot);
            }
            LastRefreshText = $"最后刷新：{_clock.LocalNow:yyyy-MM-dd HH:mm:ss} · 诊断不包含正文、密钥或完整文件路径";
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

    private async Task RevealMcpTokenAsync()
    {
        if (_mcp is null)
        {
            return;
        }

        try
        {
            McpTokenText = await _mcp.RevealTokenAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            ErrorMessage = "无法读取本地 MCP 令牌，请检查系统密钥库权限。";
        }
    }

    private async Task RotateMcpTokenAsync()
    {
        if (_mcp is null)
        {
            return;
        }

        try
        {
            McpTokenText = await _mcp.RotateTokenAsync(CancellationToken.None);
            McpStatusText = DiagnosticsDisplayFormatter.FormatMcpStatus(_mcp.GetSnapshot());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            ErrorMessage = "无法轮换本地 MCP 令牌，请检查系统密钥库权限。";
        }
    }

    private async Task ToggleMcpEnabledAsync()
    {
        if (_mcp is null)
        {
            return;
        }

        try
        {
            await _mcp.SetEnabledAsync(!McpEnabled, CancellationToken.None);
            McpEnabled = _mcp.GetSnapshot().Enabled;
            McpStatusText = DiagnosticsDisplayFormatter.FormatMcpStatus(_mcp.GetSnapshot());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            ErrorMessage = "无法更新本地 MCP 开关，请检查权限或端口占用。";
            // Revert the optimistic toggle so the control reflects the real (unchanged) state.
            OnPropertyChanged(nameof(McpEnabled));
        }
    }

    private string FormatRetentionPolicy()
    {
        if (_retentionSettings is null)
        {
            return "保留策略未接入当前运行时";
        }

        try
        {
            var settings = _retentionSettings.Get();
            var cap = settings.MaxStorageGigabytes <= 0 ? "不限" : $"{settings.MaxStorageGigabytes} GB";
            return $"媒体保留 {settings.MediaRetentionDays} 天 · 命令音频 {settings.CommandAudioRetentionDays} 天 · 存储上限 {cap}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "保留策略读取失败";
        }
    }

    private async Task RunRetentionSweepAsync()
    {
        if (_retention is null || IsLoading)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var purged = await _retention.RunSweepNowAsync(CancellationToken.None);
            var metrics = _retention.GetMetricsSnapshot();
            RetentionText = $"已扫描 {metrics.TotalSweeps} 次 · 安全擦除过期或超限媒体 {metrics.TotalPurgedBlobs} 个";
            RetentionStatusText = purged == 0
                ? "清理完成：没有需要擦除的媒体。"
                : $"清理完成：已擦除 {purged} 个过期或超限媒体。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Core.Exceptions.AiAssistantDomainException)
        {
            ErrorMessage = "清理未完成，请检查数据库状态与磁盘权限。";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string FormatModelSummary(ModelCapabilitySnapshot model)
    {
        var chat = string.IsNullOrWhiteSpace(model.ChatModel) ? "未配置" : model.ChatModel;
        var embedding = string.IsNullOrWhiteSpace(model.EmbeddingModel) ? "未配置" : model.EmbeddingModel;
        var summary = $"{(model.ChatAvailable ? "可用" : "降级")} · 对话 {chat} · 向量 {embedding}";
        return model.ChatAvailable
            ? summary
            : $"{summary} · {model.Diagnostic}";
    }

    private void ExecuteCapabilityAction(CapabilityAction? action)
    {
        switch (action?.Kind)
        {
            case CapabilityActionKind.OpenPermissionSettings:
                OpenSettingsUri(CapabilityActionResolver.SettingsUri(action.PermissionTarget));
                break;
            case CapabilityActionKind.OpenVoicePage:
                VoicePageRequested?.Invoke(this, EventArgs.Empty);
                break;
            case CapabilityActionKind.RetryDiagnostics:
                _ = RefreshCommand.ExecuteAsync(null);
                break;
        }
    }

    private static void OpenSettingsUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri)
            {
                UseShellExecute = true
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 无法打开系统设置时静默降级。
        }
        catch (InvalidOperationException)
        {
            // 环境缺少默认应用关联时静默降级。
        }
    }

    private async Task ExportPreviewAsync()
    {
        if (!HasExportPreview)
        {
            ErrorMessage = "请先生成脱敏导出预览。";
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            // Derive every format from the exact preview the user reviewed; JSON is written verbatim.
            var preview = ExportPreviewJson;
            string path;
            if (SaveFilePicker is not null)
            {
                var types = new List<SaveFileType>
                {
                    new("Markdown", "md"),
                    new("SVG 图表", "svg"),
                    new("PNG 图片", "png"),
                    new("PDF 文档", "pdf"),
                    new("JSON", "json")
                };
                var picked = await SaveFilePicker.PickSaveFileAsync(
                    $"AiAssistant-诊断导出-{_clock.LocalNow:yyyyMMdd-HHmmss}.json",
                    types);
                if (picked is null)
                {
                    ExportStatusText = "已取消保存，预览仍保留。";
                    return;
                }

                path = await _diagnostics.ExportPreviewAsAsync(
                    picked.Path,
                    preview,
                    ReportExportFormatExtensions.FromExtension(picked.Extension),
                    CancellationToken.None);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_exportDirectory))
                {
                    ErrorMessage = "未配置导出目录。";
                    return;
                }

                var export = await _diagnostics.ExportPreviewAsync(
                    _exportDirectory,
                    preview,
                    CancellationToken.None);
                path = export.MarkdownPath;
                ExportStatusText = export.SvgPath is null
                    ? $"已导出脱敏诊断（Markdown / JSON）：{path}"
                    : $"已导出脱敏诊断（Markdown / SVG / PNG / PDF / JSON）：{path}";
                return;
            }

            ExportStatusText = $"已导出脱敏诊断：{path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Core.Exceptions.AiAssistantDomainException)
        {
            ErrorMessage = "诊断导出失败，请检查目标目录权限。";
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
            ExportStatusText = "预览已生成，确认后点击「保存为…」选择格式写盘。";
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
