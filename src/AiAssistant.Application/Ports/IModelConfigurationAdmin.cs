

namespace AiAssistant.Application.Ports;

/// <summary>
/// UI-facing administration of model endpoints. The user can configure the chat endpoint and the
/// embedding endpoint independently (each with its own tier / base URL / model name) and persist
/// them to models.json.
/// </summary>
public interface IModelConfigurationAdmin
{
    /// <summary>Current chat and embedding endpoint settings (what the UI shows and edits).</summary>
    ModelConfigurationView Current { get; }

    /// <summary>Persist the edited settings. Applied on next app start.</summary>
    Task SaveAsync(ModelConfigurationView update, CancellationToken ct);
}
