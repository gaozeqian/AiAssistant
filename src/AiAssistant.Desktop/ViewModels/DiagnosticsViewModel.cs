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
    private readonly ILocalMcpControl? _mcp;
    private readonly IModelSecretAdmin? _secrets;
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
    private string _permissionGuidanceText = "尚未检查平台权限";
    private string _exportStatusText = string.Empty;
    private string _secretName = string.Empty;
    private string _secretValue = string.Empty;
    private string? _selectedSecret;
    private string _secretStatus = "密钥值写入后不可读取；models.json 通过 apiKeySecret 引用名称。";
    private bool _isLoading;

    public DiagnosticsViewModel(
        DiagnosticsUseCase diagnostics,
        ILocalMcpControl? mcp = null,
        string? exportDirectory = null,
        IModelSecretAdmin? secrets = null)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _mcp = mcp;
        _exportDirectory = exportDirectory;
        _secrets = secrets;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        GeneratePreviewCommand = new AsyncRelayCommand(GenerateExportPreviewAsync);
        ExportPreviewCommand = new AsyncRelayCommand(ExportPreviewAsync);
        RevealMcpTokenCommand = new AsyncRelayCommand(RevealMcpTokenAsync);
        RotateMcpTokenCommand = new AsyncRelayCommand(RotateMcpTokenAsync);
        SaveSecretCommand = new AsyncRelayCommand(SaveSecretAsync);
        RefreshSecretsCommand = new AsyncRelayCommand(() => RefreshSecretNamesAsync(updateStatus: true));
        DeleteSecretCommand = new AsyncRelayCommand(DeleteSecretAsync);
    }

    public ObservableCollection<CapabilityItem> Capabilities { get; } = [];

    public ObservableCollection<ReportJobItem> RecentJobs { get; } = [];

    public ObservableCollection<DiagnosticLogItem> RecentLogs { get; } = [];

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand GeneratePreviewCommand { get; }

    public IAsyncRelayCommand ExportPreviewCommand { get; }

    public string PermissionGuidanceText
    {
        get => _permissionGuidanceText;
        private set => SetProperty(ref _permissionGuidanceText, value);
    }

    public string ExportStatusText
    {
        get => _exportStatusText;
        private set => SetProperty(ref _exportStatusText, value);
    }

    public IAsyncRelayCommand RevealMcpTokenCommand { get; }

    public IAsyncRelayCommand RotateMcpTokenCommand { get; }

    public IAsyncRelayCommand SaveSecretCommand { get; }

    public IAsyncRelayCommand RefreshSecretsCommand { get; }

    public IAsyncRelayCommand DeleteSecretCommand { get; }

    public ObservableCollection<string> SecretNames { get; } = [];

    public bool HasSecretAdmin => _secrets is not null;

    public string SecretName
    {
        get => _secretName;
        set => SetProperty(ref _secretName, value);
    }

    public string SecretValue
    {
        get => _secretValue;
        set => SetProperty(ref _secretValue, value);
    }

    public string? SelectedSecret
    {
        get => _selectedSecret;
        set => SetProperty(ref _selectedSecret, value);
    }

    public string SecretStatus
    {
        get => _secretStatus;
        private set => SetProperty(ref _secretStatus, value);
    }

    public string McpStatusText
    {
        get => _mcpStatusText;
        private set => SetProperty(ref _mcpStatusText, value);
    }

    public string McpTokenText
    {
        get => _mcpTokenText;
        private set => SetProperty(ref _mcpTokenText, value);
    }

    public bool HasMcpControl => _mcp is not null;

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
            ModelText = snapshot.ModelCapability is null
                ? "未配置模型网关"
                : $"{(snapshot.ModelCapability.ChatAvailable ? "可用" : "降级")} · Chat {snapshot.ModelCapability.ChatModel ?? "未配置"} · Embedding {snapshot.ModelCapability.EmbeddingModel} · {snapshot.ModelCapability.Diagnostic}";
            ModelEndpointsText = snapshot.ModelEndpoints is { Count: > 0 }
                ? string.Join(Environment.NewLine, snapshot.ModelEndpoints.Select(endpoint =>
                    $"{endpoint.Tier}: chat={endpoint.ChatAvailable} stream={endpoint.StreamingAvailable} embed={endpoint.EmbeddingsAvailable} json={endpoint.StructuredOutputAvailable} tools={endpoint.ToolCallingAvailable} · {endpoint.Diagnostic}"))
                : "尚未探测或没有可用端点";

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

            McpStatusText = _mcp is null
                ? "本地 MCP 未接入"
                : BuildMcpStatus(_mcp.GetSnapshot());
            await RefreshSecretNamesAsync(updateStatus: false);
            var needsAttention = snapshot.Capabilities.Where(item => item.Status is not CapabilityStatus.Supported).ToArray();
            PermissionGuidanceText = needsAttention.Length == 0
                ? "当前平台权限齐全，无需额外授权。"
                : string.Join(
                    Environment.NewLine,
                    needsAttention.Select(item => $"{item.Kind}：{item.RemediationHint ?? item.Summary}"));
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

    private static string BuildMcpStatus(LocalMcpSnapshot snapshot) => snapshot switch
    {
        { Enabled: false } => "默认关闭（设置 AIASSISTANT_MCP_ENABLED=true 启用，仅监听 127.0.0.1）",
        { Running: false } => "已启用但监听未启动",
        _ => $"运行中 · {snapshot.Endpoint} · 请求 {snapshot.RequestCount} · 未授权拒绝 {snapshot.UnauthorizedCount} · 限流 {snapshot.RateLimitedCount} · 工具调用 {snapshot.ToolCallCount}"
    };

    private async Task SaveSecretAsync()
    {
        if (_secrets is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SecretName) || string.IsNullOrWhiteSpace(SecretValue))
        {
            SecretStatus = "请填写密钥名称和值。";
            return;
        }

        try
        {
            var name = SecretName.Trim();
            await _secrets.SetAsync(name, SecretValue, CancellationToken.None);
            SecretValue = string.Empty;
            await RefreshSecretNamesAsync(updateStatus: false);
            SecretStatus = $"已加密保存 {name}；值不可再读取。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            SecretStatus = "保存失败，请检查系统密钥库权限。";
        }
    }

    private async Task RefreshSecretNamesAsync(bool updateStatus)
    {
        if (_secrets is null)
        {
            return;
        }

        try
        {
            var names = await _secrets.ListNamesAsync(CancellationToken.None);
            SecretNames.Clear();
            foreach (var name in names)
            {
                SecretNames.Add(name);
            }

            if (updateStatus)
            {
                SecretStatus = names.Count == 0
                    ? "尚未保存模型密钥。"
                    : $"已保存 {names.Count} 个密钥（值不可读取）。";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            if (updateStatus)
            {
                SecretStatus = "无法读取密钥列表，请检查系统密钥库权限。";
            }
        }
    }

    private async Task DeleteSecretAsync()
    {
        if (_secrets is null || string.IsNullOrWhiteSpace(SelectedSecret))
        {
            SecretStatus = "请选择要删除的密钥。";
            return;
        }

        try
        {
            var name = SelectedSecret;
            var deleted = await _secrets.DeleteAsync(name, CancellationToken.None);
            await RefreshSecretNamesAsync(updateStatus: false);
            SecretStatus = deleted ? $"已删除 {name}。" : $"{name} 不存在。";
            SelectedSecret = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            SecretStatus = "删除失败，请检查系统密钥库权限。";
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
            McpStatusText = BuildMcpStatus(_mcp.GetSnapshot());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            ErrorMessage = "无法轮换本地 MCP 令牌，请检查系统密钥库权限。";
        }
    }

    private async Task ExportPreviewAsync()
    {
        if (string.IsNullOrWhiteSpace(_exportDirectory))
        {
            ErrorMessage = "未配置导出目录。";
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var preview = await _diagnostics.BuildSanitizedExportPreviewAsync();
            Directory.CreateDirectory(_exportDirectory);
            var path = Path.Combine(
                _exportDirectory,
                $"AiAssistant-诊断导出-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
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
        CapabilityKind.ModelGateway => "本机模型",
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
