namespace AiAssistant.Application.Ports;

/// <summary>
/// UI-facing administration of the local model endpoint. Lets the user change the chat / embedding
/// model names and base URL of the LocalOffline endpoint and persist them to models.json.
/// </summary>
public interface IModelConfigurationAdmin
{
    /// <summary>Current LocalOffline endpoint settings (what the UI shows and edits).</summary>
    ModelConfigurationView Current { get; }

    /// <summary>Persist the edited settings. Applied on next app start.</summary>
    Task SaveAsync(ModelConfigurationView update, CancellationToken ct);
}

public sealed record ModelConfigurationView(
    string BaseUrl,
    string ChatModel,
    string EmbeddingModel);
