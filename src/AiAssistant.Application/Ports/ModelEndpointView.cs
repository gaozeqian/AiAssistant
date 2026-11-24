
namespace AiAssistant.Application.Ports;

/// <summary>One model endpoint as edited from the UI (chat or embedding).</summary>
/// <param name="BaseUrl">Endpoint base address.</param>
/// <param name="Model">Chat or embedding model name.</param>
/// <param name="Tier">LocalOffline / CorporateIntranet / PublicCloud.</param>
/// <param name="Think">Whether the chat model may reason before answering.</param>
/// <param name="ThinkingLevel">Optional thinking effort for chat.</param>
/// <param name="ApiKeySecret">Name of the stored secret; the value is never read back.</param>
/// <param name="ApiKey">Write-only plaintext key supplied by the user for this save.</param>
/// <param name="ClearApiKey">Deletes the stored secret and its reference.</param>
public sealed record ModelEndpointView(
    string BaseUrl,
    string Model,
    string Tier,
    bool Think = false,
    string? ThinkingLevel = null,
    string? ApiKeySecret = null,
    string? ApiKey = null,
    bool ClearApiKey = false);
