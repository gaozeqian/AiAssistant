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
    private string _chatBaseUrl = ModelDefaults.OllamaBaseUrl;
    private string _chatModel = string.Empty;
    private ModelTierOption _chatTier = new("LocalOffline", "本机");
    private bool _chatThink;
    private ModelThinkingLevelOption _chatThinkingLevel = new("low", "低");
    private string _embeddingBaseUrl = ModelDefaults.OllamaBaseUrl;
    private string _embeddingModel = string.Empty;
    private ModelTierOption _embeddingTier = new("LocalOffline", "本机");
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
            _embeddingBaseUrl = view.Embedding.BaseUrl;
            _embeddingModel = view.Embedding.Model;
            _embeddingTier = TierOptions.First(option => option.Value == view.Embedding.Tier);
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
            if (SetProperty(ref _chatTier, value) &&
                value.Value != "LocalOffline" &&
                string.IsNullOrWhiteSpace(ChatBaseUrl))
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
            if (SetProperty(ref _embeddingTier, value) &&
                value.Value != "LocalOffline" &&
                string.IsNullOrWhiteSpace(EmbeddingBaseUrl))
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
}
