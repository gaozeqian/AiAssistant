namespace AiAssistant.Application.Ports;

/// <summary>
/// UI-facing administration of model endpoints. Lets the user change the chat / embedding model
/// names, base URL and tier (local / intranet / public cloud) of the editable endpoint and persist
/// them to models.json.
/// </summary>
public interface IModelConfigurationAdmin
{
    /// <summary>Current endpoint settings (what the UI shows and edits).</summary>
    ModelConfigurationView Current { get; }

    /// <summary>Persist the edited settings. Applied on next app start.</summary>
    Task SaveAsync(ModelConfigurationView update, CancellationToken ct);
}

public sealed record ModelConfigurationView(
    string BaseUrl,
    string ChatModel,
    string EmbeddingModel,
    string Tier);
