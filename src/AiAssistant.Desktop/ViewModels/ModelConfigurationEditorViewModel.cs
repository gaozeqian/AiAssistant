using AiAssistant.Application.Ports;
using AiAssistant.Core.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiAssistant.Desktop.ViewModels;

/// <summary>
/// Edits the local chat/embedding endpoint configuration. Extracted from DiagnosticsViewModel so
/// model configuration state and persistence have a single owner.
/// </summary>
public sealed class ModelConfigurationEditorViewModel : ObservableObject
{
    private readonly IModelConfigurationAdmin? _modelConfiguration;
    private string _chatBaseUrl = ModelDefaults.LocalBaseUrl;
    private string _chatModel = string.Empty;
    private ModelTierOption _chatTier = new("LocalOffline", "本机");
    private bool _chatThink;
    private ModelThinkingLevelOption _chatThinkingLevel = new("low", "低");
    private string _chatApiKey = string.Empty;
    private bool _clearChatApiKey;
    private string? _chatApiKeySecret;
    private string _embeddingBaseUrl = ModelDefaults.LocalBaseUrl;
    private string _embeddingModel = string.Empty;
    private ModelTierOption _embeddingTier = new("LocalOffline", "本机");
    private string _embeddingApiKey = string.Empty;
    private bool _clearEmbeddingApiKey;
    private string? _embeddingApiKeySecret;
    private string _modelConfigStatus = "保存后需重启应用生效。";

    public ModelConfigurationEditorViewModel(IModelConfigurationAdmin? modelConfiguration)
    {
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
            _chatApiKeySecret = view.Chat.ApiKeySecret;
            _embeddingBaseUrl = view.Embedding.BaseUrl;
            _embeddingModel = view.Embedding.Model;
            _embeddingTier = TierOptions.First(option => option.Value == view.Embedding.Tier);
            _embeddingApiKeySecret = view.Embedding.ApiKeySecret;
        }

        SaveModelConfigurationCommand = new AsyncRelayCommand(SaveAsync);
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
            if (SetProperty(ref _chatTier, value))
            {
                ChatBaseUrl = SuggestBaseUrl(value.Value, ChatBaseUrl);
            }
        }
    }

    /// <summary>Write-only; an empty value keeps the stored secret.</summary>
    public string ChatApiKey
    {
        get => _chatApiKey;
        set => SetProperty(ref _chatApiKey, value);
    }

    public bool ClearChatApiKey
    {
        get => _clearChatApiKey;
        set => SetProperty(ref _clearChatApiKey, value);
    }

    public string ChatApiKeyHint => string.IsNullOrWhiteSpace(_chatApiKeySecret)
        ? "未保存接口密钥；公网/内网端点通常需要。"
        : $"已保存密钥「{_chatApiKeySecret}」（值不可读取）。";

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
            if (SetProperty(ref _embeddingTier, value))
            {
                EmbeddingBaseUrl = SuggestBaseUrl(value.Value, EmbeddingBaseUrl);
            }
        }
    }

    /// <summary>Write-only; an empty value keeps the stored secret.</summary>
    public string EmbeddingApiKey
    {
        get => _embeddingApiKey;
        set => SetProperty(ref _embeddingApiKey, value);
    }

    public bool ClearEmbeddingApiKey
    {
        get => _clearEmbeddingApiKey;
        set => SetProperty(ref _clearEmbeddingApiKey, value);
    }

    public string EmbeddingApiKeyHint => string.IsNullOrWhiteSpace(_embeddingApiKeySecret)
        ? "未保存接口密钥；公网/内网端点通常需要。"
        : $"已保存密钥「{_embeddingApiKeySecret}」（值不可读取）。";

    public IReadOnlyList<ModelTierOption> TierOptions { get; } =
    [
        new("LocalOffline", "本机"),
        new("CorporateIntranet", "企业内网"),
        new("PublicCloud", "公网 API")
    ];

    /// <summary>Reasoning effort sent as <c>reasoning_effort</c> when 允许思考 is enabled.</summary>
    public IReadOnlyList<ModelThinkingLevelOption> ThinkingLevelOptions { get; } =
    [
        new("low", "低"),
        new("medium", "中"),
        new("high", "高")
    ];

    /// <summary>Whether the chat model may reason before answering (maps to <c>reasoning_effort</c>).</summary>
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

    /// <summary>
    /// Switching tiers should not leave a loopback address on a remote tier (or an https address on
    /// the local tier); only the obvious placeholder cases are rewritten, a real user URL is kept.
    /// </summary>
    private static string SuggestBaseUrl(string tier, string current)
    {
        var isEmpty = string.IsNullOrWhiteSpace(current);
        if (tier != "LocalOffline")
        {
            var isLoopback = Uri.TryCreate(current, UriKind.Absolute, out var uri) && uri.IsLoopback;
            return isEmpty || isLoopback ? "https://" : current;
        }

        var isHttps = Uri.TryCreate(current, UriKind.Absolute, out var httpsUri) &&
                      httpsUri.Scheme == Uri.UriSchemeHttps;
        return isEmpty || isHttps ? ModelDefaults.LocalBaseUrl : current;
    }

    private async Task SaveAsync()
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
                        ChatThinkingLevel.Value,
                        _chatApiKeySecret,
                        ChatApiKey,
                        ClearChatApiKey),
                    new ModelEndpointView(
                        EmbeddingBaseUrl,
                        EmbeddingModel,
                        EmbeddingTier.Value,
                        ApiKeySecret: _embeddingApiKeySecret,
                        ApiKey: EmbeddingApiKey,
                        ClearApiKey: ClearEmbeddingApiKey)),
                CancellationToken.None);

            // Clear the write-only inputs and refresh the "stored secret" hints from the admin.
            ChatApiKey = string.Empty;
            EmbeddingApiKey = string.Empty;
            ClearChatApiKey = false;
            ClearEmbeddingApiKey = false;
            _chatApiKeySecret = _modelConfiguration.Current.Chat.ApiKeySecret;
            _embeddingApiKeySecret = _modelConfiguration.Current.Embedding.ApiKeySecret;
            OnPropertyChanged(nameof(ChatApiKeyHint));
            OnPropertyChanged(nameof(EmbeddingApiKeyHint));
            ModelConfigStatus = "已保存模型配置；重启应用后生效。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ModelConfigStatus = ex.Message;
        }
    }
}
