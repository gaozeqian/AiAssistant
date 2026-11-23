using System.Diagnostics;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Models;

/// <summary>Direct local model gateway; tiers above LocalOffline are rejected until S6 policy exists.</summary>
public sealed class LocalModelGateway(
    HttpModelBackend backend,
    ModelGatewayOptions options,
    ILogger<LocalModelGateway> logger) : IModelGateway
{
    private volatile ModelCapabilitySnapshot _snapshot = new(
        false,
        false,
        string.IsNullOrWhiteSpace(options.ChatModel) ? null : options.ChatModel,
        options.EmbeddingModel,
        ModelEndpointTier.LocalOffline,
        "Model endpoint has not been probed.");

    public CapabilityState Capability => !options.IsEndpointPermitted
        ? CapabilityState.DisabledByPolicy(CapabilityKind.ModelGateway, "Remote model endpoints require explicit S6 policy")
        : _snapshot.ChatAvailable
            ? CapabilityState.Supported(CapabilityKind.ModelGateway, $"Local model ready ({_snapshot.ChatModel})")
            : CapabilityState.Degraded(CapabilityKind.ModelGateway, _snapshot.Diagnostic);

    public string ChatModelId => options.ChatModel;
    public string EmbeddingModelId => options.EmbeddingModel;

    public async Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureLocalTier(request.Tier);
        if (!_snapshot.ChatAvailable)
        {
            await ProbeAsync(ct);
        }

        if (!_snapshot.ChatAvailable)
        {
            throw new ModelGatewayException(_snapshot.Diagnostic);
        }

        var started = Stopwatch.GetTimestamp();
        var response = await backend.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, request.SystemPrompt),
                new ChatMessage(ChatRole.User, request.UserPrompt)
            ],
            new ChatOptions
            {
                ModelId = options.ChatModel,
                Temperature = (float)request.Temperature,
                MaxOutputTokens = request.MaxOutputTokens
            },
            ct);
        var text = response.Messages.LastOrDefault()?.Text ?? string.Empty;
        return new ModelCompletionResult(
            text,
            options.ChatModel,
            ModelEndpointTier.LocalOffline,
            RedactionApplied: false,
            Stopwatch.GetElapsedTime(started));
    }

    public async Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureLocalTier(request.Tier);
        if (request.Inputs.Count == 0)
        {
            return new ModelEmbeddingResult([], options.EmbeddingModel, 0);
        }

        var result = await backend.GenerateAsync(
            request.Inputs,
            new EmbeddingGenerationOptions { ModelId = options.EmbeddingModel },
            ct);
        var vectors = result.Select(embedding => embedding.Vector).ToArray();
        var dimensions = vectors.Length == 0 ? 0 : vectors[0].Length;
        return new ModelEmbeddingResult(vectors, options.EmbeddingModel, dimensions);
    }

    public async Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct)
    {
        if (!options.IsEndpointPermitted)
        {
            _snapshot = _snapshot with { Diagnostic = "Remote endpoint is disabled by local-only policy." };
            return _snapshot;
        }

        var probe = await backend.ProbeAsync(ct);
        _snapshot = new ModelCapabilitySnapshot(
            probe.ChatAvailable,
            probe.EmbeddingsAvailable,
            string.IsNullOrWhiteSpace(options.ChatModel) ? null : options.ChatModel,
            options.EmbeddingModel,
            ModelEndpointTier.LocalOffline,
            probe.Diagnostic);
        if (!probe.ChatAvailable)
        {
            logger.LogInformation("Local model endpoint is not ready: {Diagnostic}", probe.Diagnostic);
        }

        return _snapshot;
    }

    private static void EnsureLocalTier(ModelEndpointTier tier)
    {
        if (tier != ModelEndpointTier.LocalOffline)
        {
            throw new ModelGatewayException("Only LocalOffline model tier is enabled in S5.");
        }
    }
}

public sealed class InMemoryModelAuditSink : IModelAuditSink
{
    private const int Capacity = 200;
    private readonly Lock _sync = new();
    private readonly Queue<ModelAuditEntry> _entries = new(Capacity);

    public void Record(ModelAuditEntry entry)
    {
        lock (_sync)
        {
            if (_entries.Count >= Capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
        }
    }

    public IReadOnlyList<ModelAuditEntry> GetRecent(int maxCount = 50)
    {
        lock (_sync)
        {
            return _entries.TakeLast(Math.Clamp(maxCount, 1, Capacity)).ToArray();
        }
    }
}

public sealed class TimeoutModelGateway(IModelGateway inner, ModelGatewayOptions options) : IModelGateway
{
    public CapabilityState Capability => inner.Capability;
    public string ChatModelId => inner.ChatModelId;
    public string EmbeddingModelId => inner.EmbeddingModelId;

    public async Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.RequestTimeout);
        try
        {
            return await inner.CompleteAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModelGatewayException("Model request timed out.", null);
        }
    }

    public async Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.RequestTimeout);
        try
        {
            return await inner.EmbedAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModelGatewayException("Embedding request timed out.", null);
        }
    }

    public Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct) => inner.ProbeAsync(ct);
}

public sealed class RedactingModelGateway(IModelGateway inner, ITextRedactor redactor) : IModelGateway
{
    public CapabilityState Capability => inner.Capability;
    public string ChatModelId => inner.ChatModelId;
    public string EmbeddingModelId => inner.EmbeddingModelId;

    public async Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct)
    {
        var system = redactor.Redact(request.SystemPrompt);
        var user = redactor.Redact(request.UserPrompt);
        var sanitized = request with { SystemPrompt = system.Text, UserPrompt = user.Text };
        var result = await inner.CompleteAsync(sanitized, ct);
        return result with { RedactionApplied = result.RedactionApplied || system.Count + user.Count > 0 };
    }

    public async Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct)
    {
        var sanitized = request with
        {
            Inputs = request.Inputs.Select(input => redactor.Redact(input).Text).ToArray()
        };
        return await inner.EmbedAsync(sanitized, ct);
    }

    public Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct) => inner.ProbeAsync(ct);
}

public sealed class AuditedModelGateway(IModelGateway inner, IModelAuditSink auditSink) : IModelGateway
{
    public CapabilityState Capability => inner.Capability;
    public string ChatModelId => inner.ChatModelId;
    public string EmbeddingModelId => inner.EmbeddingModelId;

    public async Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await inner.CompleteAsync(request, ct);
            auditSink.Record(new ModelAuditEntry(
                TimeProvider.System.GetUtcNow(),
                request.Purpose,
                result.Tier,
                result.ModelId,
                request.SystemPrompt.Length + request.UserPrompt.Length,
                result.Text.Length,
                result.RedactionApplied,
                true,
                null,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            return result;
        }
        catch (Exception ex)
        {
            auditSink.Record(new ModelAuditEntry(
                TimeProvider.System.GetUtcNow(),
                request.Purpose,
                request.Tier,
                inner.ChatModelId,
                request.SystemPrompt.Length + request.UserPrompt.Length,
                0,
                false,
                false,
                ex.GetType().Name,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            throw;
        }
    }

    public async Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await inner.EmbedAsync(request, ct);
            auditSink.Record(new ModelAuditEntry(
                TimeProvider.System.GetUtcNow(),
                request.Purpose,
                request.Tier,
                result.ModelId,
                request.Inputs.Sum(input => input.Length),
                0,
                false,
                true,
                null,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            return result;
        }
        catch (Exception ex)
        {
            auditSink.Record(new ModelAuditEntry(
                TimeProvider.System.GetUtcNow(),
                request.Purpose,
                request.Tier,
                inner.EmbeddingModelId,
                request.Inputs.Sum(input => input.Length),
                0,
                false,
                false,
                ex.GetType().Name,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            throw;
        }
    }

    public Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct) => inner.ProbeAsync(ct);
}
