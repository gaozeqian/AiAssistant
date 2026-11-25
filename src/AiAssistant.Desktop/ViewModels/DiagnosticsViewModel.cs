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
    private readonly DiagnosticsUseCase _diagnostics;
    private readonly IClock _clock;
    private readonly ILocalMcpControl? _mcp;
    private readonly string? _exportDirectory;
    private string _platformName = "正在检测平台能力…";
    private string _databaseText = "尚未检查";
    private string _pipelineText = string.Empty;
    private string _retentionText = "尚未执行清理扫描";
    private string _modelText = "尚未探测本机模型";
    private string _modelEndpointsText = "尚未探测端点能力";
    private string _exportPreviewJson = string.Empty;
    private string _lastRefreshText = "尚未刷新";
    private string _errorMessage = string.Empty;
    private string _mcpStatusText = "本地 MCP 默认关闭";
    private string _mcpTokenText = "点击“显示令牌”获取本地凭据";
    private bool _mcpEnabled;
    private string _exportStatusText = string.Empty;
    private bool _isLoading;

    public DiagnosticsViewModel(
        DiagnosticsUseCase diagnostics,
        ILocalMcpControl? mcp = null,
        string? exportDirectory = null,
        IModelConfigurationAdmin? modelConfiguration = null,
        IClock? clock = null)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _clock = clock ?? new SystemClock();
        _mcp = mcp;
        _exportDirectory = exportDirectory;
        ModelConfig = new ModelConfigurationEditorViewModel(modelConfiguration);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        GeneratePreviewCommand = new AsyncRelayCommand(GenerateExportPreviewAsync);
        ExportPreviewCommand = new AsyncRelayCommand(ExportPreviewAsync);
        RevealMcpTokenCommand = new AsyncRelayCommand(RevealMcpTokenAsync);
        RotateMcpTokenCommand = new AsyncRelayCommand(RotateMcpTokenAsync);
        ToggleMcpEnabledCommand = new AsyncRelayCommand(ToggleMcpEnabledAsync);
        OpenSettingsCommand = new RelayCommand(OpenSystemSettings);
        _mcpEnabled = _mcp?.GetSnapshot().Enabled ?? false;
    }

    public ObservableCollection<CapabilityItem> Capabilities { get; } = [];

    public ObservableCollection<ReportJobItem> RecentJobs { get; } = [];

    public ObservableCollection<DiagnosticLogItem> RecentLogs { get; } = [];

    public ModelConfigurationEditorViewModel ModelConfig { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand GeneratePreviewCommand { get; }

    public IAsyncRelayCommand ExportPreviewCommand { get; }

    public IAsyncRelayCommand RevealMcpTokenCommand { get; }

    public IAsyncRelayCommand RotateMcpTokenCommand { get; }

    public IAsyncRelayCommand ToggleMcpEnabledCommand { get; }

    public IRelayCommand OpenSettingsCommand { get; }

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
        private set => SetProperty(ref _exportStatusText, value);
    }

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
                ? $"正常 · {snapshot.DatabaseHealth.EventCount} 条事件 · {snapshot.DatabaseHealth.EncryptedBlobCount} 个加密媒体"
                : "数据库健康检查未通过";
            PipelineText = $"队列 {snapshot.PipelineMetrics.QueuedDataItems} / 已保存 {snapshot.PipelineMetrics.PersistedEvents} / 控制命令 {snapshot.PipelineMetrics.ControlCommandsProcessed}";
            RetentionText = snapshot.RetentionMetrics is null
                ? "默认保留策略已配置"
                : $"已扫描 {snapshot.RetentionMetrics.TotalSweeps} 次 · 安全擦除过期媒体 {snapshot.RetentionMetrics.TotalPurgedBlobs} 个";
            ModelText = snapshot.ModelCapability is null
                ? "未配置模型网关"
                : FormatModelSummary(snapshot.ModelCapability);
            ModelEndpointsText = snapshot.ModelEndpoints is { Count: > 0 }
                ? string.Join(
                    Environment.NewLine,
                    snapshot.ModelEndpoints.Select(DiagnosticsDisplayFormatter.FormatEndpoint))
                : "尚未探测端点能力";

            DiagnosticsDisplayFormatter.Replace(Capabilities, snapshot.Capabilities.Select(capability => new CapabilityItem(capability, OpenSettingsCommand)));
            DiagnosticsDisplayFormatter.Replace(RecentJobs, snapshot.RecentJobs.Select(job => new ReportJobItem(job)));
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

    private static string FormatModelSummary(ModelCapabilitySnapshot model)
    {
        var chat = string.IsNullOrWhiteSpace(model.ChatModel) ? "未配置" : model.ChatModel;
        var embedding = string.IsNullOrWhiteSpace(model.EmbeddingModel) ? "未配置" : model.EmbeddingModel;
        var summary = $"{(model.ChatAvailable ? "可用" : "降级")} · 对话 {chat} · 向量 {embedding}";
        return model.ChatAvailable
            ? summary
            : $"{summary} · {model.Diagnostic}";
    }

    private static void OpenSystemSettings()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:")
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
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var preview = await _diagnostics.BuildSanitizedExportPreviewAsync();
            string path;
            if (SaveFilePicker is not null)
            {
                var picked = await SaveFilePicker.PickSavePathAsync(
                    $"AiAssistant-诊断导出-{_clock.LocalNow:yyyyMMdd-HHmmss}.json",
                    [new SaveFileType("JSON", "json")]);
                if (picked is null)
                {
                    ExportPreviewJson = preview;
                    ExportStatusText = "已生成预览，未选择保存位置。";
                    return;
                }

                path = picked;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_exportDirectory))
                {
                    ErrorMessage = "未配置导出目录。";
                    return;
                }

                Directory.CreateDirectory(_exportDirectory);
                path = Path.Combine(
                    _exportDirectory,
                    $"AiAssistant-诊断导出-{_clock.LocalNow:yyyyMMdd-HHmmss}.json");
            }

            await _diagnostics.ExportConfirmedPreviewAsync(path, preview, CancellationToken.None);
            ExportPreviewJson = preview;
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
