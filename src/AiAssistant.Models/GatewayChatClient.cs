using System.Runtime.CompilerServices;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using Microsoft.Extensions.AI;

namespace AiAssistant.Models;

/// <summary>
/// Standard Microsoft.Extensions.AI facade over the policy-enforced IModelGateway.
/// Direct consumers cannot bypass tier policy, redaction or audit.
/// </summary>
public sealed class GatewayChatClient(
    IModelGateway gateway,
    ModelEndpointTier defaultTier) : IChatClient, IEmbeddingGenerator<string, Embedding<float>>
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages.ToArray();
        var system = string.Join("\n", list.Where(message => message.Role == ChatRole.System).Select(message => message.Text));
        var user = string.Join("\n", list.Where(message => message.Role != ChatRole.System).Select(message => message.Text));
        var result = await gateway.CompleteAsync(
            new ModelCompletionRequest(
                system,
                user,
                defaultTier,
                Purpose: "meai-direct",
                Temperature: options?.Temperature ?? 0.2,
                MaxOutputTokens: options?.MaxOutputTokens ?? 2048),
            cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, result.Text)) { ModelId = result.ModelId };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var message in response.Messages)
        {
            yield return new ChatResponseUpdate(message.Role, message.Text);
        }
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await gateway.EmbedAsync(
            new ModelEmbeddingRequest(values.ToArray(), defaultTier, Purpose: "meai-direct"),
            cancellationToken);
        return new GeneratedEmbeddings<Embedding<float>>(
            result.Vectors.Select(vector => new Embedding<float>(vector)).ToArray());
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // Gateway lifetime is owned by the Host composition root.
    }
}
