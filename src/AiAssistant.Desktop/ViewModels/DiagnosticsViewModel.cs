using System.Collections.ObjectModel;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.Configuration;
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
    private readonly IModelConfigurationAdmin? _modelConfiguration;
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
    private string _chatBaseUrl = ModelDefaults.OllamaBaseUrl;
    private string _chatModel = string.Empty;
    private ModelTierOption _chatTier = new("LocalOffline", "本机");
    private bool _chatThink;
    private ModelThinkingLevelOption _chatThinkingLevel = new("low", "低");
    private string _embeddingBaseUrl = ModelDefaults.OllamaBaseUrl;
    private string _embeddingModel = string.Empty;
    private ModelTierOption _embeddingTier = new("LocalOffline", "本机");
    private string _modelConfigStatus = "保存后需重启应用生效。";

    public DiagnosticsViewModel(
        DiagnosticsUseCase diagnostics,
        ILocalMcpControl? mcp = null,
        string? exportDirectory = null,
        IModelSecretAdmin? secrets = null,
        IModelConfigurationAdmin? modelConfiguration = null)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _mcp = mcp;
        _exportDirectory = exportDirectory;
        _secrets = secrets;
        _modelConfiguration = modelConfiguration;
        if (modelConfiguration is not null)
        {
            var view = modelConfiguration.Current;
            _chatBaseUrl = view.Chat.BaseUrl;
            _chatModel = view.Chat.Model;
            _chatTier = TierOptions.First(option => option.Value == view.Chat.Tier);
            _chatThink = view.Chat.Think;
            _chatThinkingLevel = ThinkingLevelOptions.FirstOrDefault(option =>
                                        option.Value == (view.Chat.ThinkingLevel ?? "low"))
                                    ?? ThinkingLevelOptions[0];
            _embeddingBaseUrl = view.Embedding.BaseUrl;
            _embeddingModel = view.Embedding.Model;
            _embeddingTier = TierOptions.First(option => option.Value == view.Embedding.Tier);
        }
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        GeneratePreviewCommand = new AsyncRelayCommand(GenerateExportPreviewAsync);
        ExportPreviewCommand = new AsyncRelayCommand(ExportPreviewAsync);
        RevealMcpTokenCommand = new AsyncRelayCommand(RevealMcpTokenAsync);
        RotateMcpTokenCommand = new AsyncRelayCommand(RotateMcpTokenAsync);
        SaveSecretCommand = new AsyncRelayCommand(SaveSecretAsync);
        RefreshSecretsCommand = new AsyncRelayCommand(() => RefreshSecretNamesAsync(updateStatus: true));
        DeleteSecretCommand = new AsyncRelayCommand(DeleteSecretAsync);
        SaveModelConfigurationCommand = new AsyncRelayCommand(SaveModelConfigurationAsync);
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

    public bool HasModelConfiguration => _modelConfiguration is not null;

    public string ChatBaseUrl
    {
        get => _chatBaseUrl;
        set => SetProperty(ref _chatBaseUrl, value);
    }

    public string ChatModel
    {
        get => _chatModel;
        set => SetProperty(ref _chatModel, value);
    }

    public ModelTierOption ChatTier
    {
        get => _chatTier;
        set
        {
            if (SetProperty(ref _chatTier, value) && value.Value != "LocalOffline" && string.IsNullOrWhiteSpace(ChatBaseUrl))
            {
                ChatBaseUrl = "https://";
            }
        }
    }

    public string EmbeddingBaseUrl
    {
        get => _embeddingBaseUrl;
        set => SetProperty(ref _embeddingBaseUrl, value);
    }

    public string EmbeddingModel
    {
        get => _embeddingModel;
        set => SetProperty(ref _embeddingModel, value);
    }

    public ModelTierOption EmbeddingTier
    {
        get => _embeddingTier;
        set
        {
            if (SetProperty(ref _embeddingTier, value) && value.Value != "LocalOffline" && string.IsNullOrWhiteSpace(EmbeddingBaseUrl))
            {
                EmbeddingBaseUrl = "https://";
            }
        }
    }

    public IReadOnlyList<ModelTierOption> TierOptions { get; } =
    [
        new("LocalOffline", "本机"),
        new("CorporateIntranet", "企业内网"),
        new("PublicCloud", "公网 API")
    ];

    /// <summary>Thinking effort passed to Ollama thinking models when 允许思考 is enabled.</summary>
    public IReadOnlyList<ModelThinkingLevelOption> ThinkingLevelOptions { get; } =
    [
        new("low", "低"),
        new("medium", "中"),
        new("high", "高")
    ];

    /// <summary>Whether the chat model may reason before answering (Ollama thinking models).</summary>
    public bool ChatThink
    {
        get => _chatThink;
        set => SetProperty(ref _chatThink, value);
    }

    public ModelThinkingLevelOption ChatThinkingLevel
    {
        get => _chatThinkingLevel;
        set => SetProperty(ref _chatThinkingLevel, value);
    }

    public string ModelConfigStatus
    {
        get => _modelConfigStatus;
        private set => SetProperty(ref _modelConfigStatus, value);
    }

    public IAsyncRelayCommand SaveModelConfigurationCommand { get; }

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
                ? $"正常 · 数据库结构 v{snapshot.DatabaseHealth.SchemaVersion.Number} · {snapshot.DatabaseHealth.EventCount} 条事件 · {snapshot.DatabaseHealth.EncryptedBlobCount} 个加密媒体"
                : "数据库健康检查未通过";
            PipelineText = $"队列 {snapshot.PipelineMetrics.QueuedDataItems} / 已保存 {snapshot.PipelineMetrics.PersistedEvents} / 控制命令 {snapshot.PipelineMetrics.ControlCommandsProcessed}";
            RetentionText = snapshot.RetentionMetrics is null
                ? "默认保留策略已配置"
                : $"已扫描 {snapshot.RetentionMetrics.TotalSweeps} 次 · 安全擦除过期媒体 {snapshot.RetentionMetrics.TotalPurgedBlobs} 个";
            ModelText = snapshot.ModelCapability is null
                ? "未配置模型网关"
                : $"{(snapshot.ModelCapability.ChatAvailable ? "可用" : "降级")} · 对话 {snapshot.ModelCapability.ChatModel ?? "未配置"} · 向量 {snapshot.ModelCapability.EmbeddingModel} · {snapshot.ModelCapability.Diagnostic}";
            ModelEndpointsText = snapshot.ModelEndpoints is { Count: > 0 }
                ? string.Join(Environment.NewLine, snapshot.ModelEndpoints.Select(endpoint =>
                    $"{TierLabel(endpoint.Tier)}：对话={(endpoint.ChatAvailable ? "可用" : "不可用")} · 流式={(endpoint.StreamingAvailable ? "可用" : "不可用")} · 向量={(endpoint.EmbeddingsAvailable ? "可用" : "不可用")} · 结构化={(endpoint.StructuredOutputAvailable ? "可用" : "不可用")} · 工具={(endpoint.ToolCallingAvailable ? "可用" : "不可用")} · {endpoint.Diagnostic}"))
                : "尚未探测端点能力";

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
                    needsAttention.Select(item =>
                        $"{CapabilityItem.KindLabel(item.Kind)}：{item.RemediationHint ?? item.Summary}"));
            LastRefreshText = $"最后刷新：{TimeProvider.System.GetLocalNow():HH:mm:ss} · 诊断不包含正文、密钥或完整文件路径";
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

    private static string TierLabel(ModelEndpointTier tier) => tier switch
    {
        ModelEndpointTier.LocalOffline => "本机",
        ModelEndpointTier.CorporateIntranet => "企业内网",
        ModelEndpointTier.PublicCloud => "公网",
        _ => tier.ToString()
    };

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

    private async Task SaveModelConfigurationAsync()
    {
        if (_modelConfiguration is null)
        {
            ModelConfigStatus = "本机未启用模型配置。";
            return;
        }

        try
        {
            await _modelConfiguration.SaveAsync(
                new ModelConfigurationView(
                    new ModelEndpointView(
                        ChatBaseUrl,
                        ChatModel,
                        ChatTier.Value,
                        ChatThink,
                        ChatThinkingLevel.Value),
                    new ModelEndpointView(EmbeddingBaseUrl, EmbeddingModel, EmbeddingTier.Value)),
                CancellationToken.None);
            ModelConfigStatus = "已保存到 models.json；重启应用后生效。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ModelConfigStatus = ex.Message;
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
                $"AiAssistant-诊断导出-{TimeProvider.System.GetLocalNow():yyyyMMdd-HHmmss}.json");
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
