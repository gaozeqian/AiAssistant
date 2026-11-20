using System.Diagnostics;
using System.Text.RegularExpressions;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Models;

public sealed class ModelPolicyViolationException(string message) : Exception(message);

/// <summary>Explicit runtime consent for public-cloud use; configuration alone is never sufficient.</summary>
public sealed class PublicCloudConsentGate
{
    private readonly Lock _sync = new();
    private string? _confirmation;
    private DateTimeOffset? _confirmedAtUtc;

    public bool IsGranted
    {
        get
        {
            lock (_sync)
            {
                return _confirmation is not null;
            }
        }
    }

    public DateTimeOffset? ConfirmedAtUtc
    {
        get
        {
            lock (_sync)
            {
                return _confirmedAtUtc;
            }
        }
    }

    public void Grant(string confirmation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmation);
        lock (_sync)
        {
            _confirmation = confirmation;
            _confirmedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    public void Revoke()
    {
        lock (_sync)
        {
            _confirmation = null;
            _confirmedAtUtc = null;
        }
    }
}

/// <summary>Host-outer policy gate. Direct application use cases cannot bypass it.</summary>
public sealed class PolicyEnforcedModelGateway(
    IModelGateway inner,
    ModelPolicy policy,
    PublicCloudConsentGate consent) : IModelGateway
{
    public CapabilityState Capability => inner.Capability;
    public string ChatModelId => inner.ChatModelId;
    public string EmbeddingModelId => inner.EmbeddingModelId;

    public Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct)
    {
        EnsureAllowed(request.Tier);
        return inner.CompleteAsync(request, ct);
    }

    public Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct)
    {
        EnsureAllowed(request.Tier);
        return inner.EmbedAsync(request, ct);
    }

    public Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct)
    {
        if (policy.DefaultTier == ModelEndpointTier.PublicCloud &&
            (!policy.AllowPublicCloud || (policy.RequirePublicCloudConsent && !consent.IsGranted)))
        {
            return Task.FromResult(new ModelCapabilitySnapshot(
                false,
                false,
                null,
                string.Empty,
                ModelEndpointTier.PublicCloud,
                "Public cloud probe blocked by policy or missing consent."));
        }

        if (policy.DefaultTier == ModelEndpointTier.CorporateIntranet && !policy.AllowIntranet)
        {
            return Task.FromResult(new ModelCapabilitySnapshot(
                false,
                false,
                null,
                string.Empty,
                ModelEndpointTier.CorporateIntranet,
                "Corporate intranet probe blocked by policy."));
        }

        return inner.ProbeAsync(ct);
    }

    private void EnsureAllowed(ModelEndpointTier tier)
    {
        switch (tier)
        {
            case ModelEndpointTier.LocalOffline:
                return;
            case ModelEndpointTier.CorporateIntranet when policy.AllowIntranet:
                return;
            case ModelEndpointTier.CorporateIntranet:
                throw new ModelPolicyViolationException("Corporate intranet tier is disabled by policy.");
            case ModelEndpointTier.PublicCloud when !policy.AllowPublicCloud:
                throw new ModelPolicyViolationException("Public cloud tier is locked by enterprise policy.");
            case ModelEndpointTier.PublicCloud when policy.RequirePublicCloudConsent && !consent.IsGranted:
                throw new ModelPolicyViolationException("Public cloud requires explicit runtime consent.");
            case ModelEndpointTier.PublicCloud:
                return;
            default:
                throw new ModelPolicyViolationException("Unknown model tier.");
        }
    }
}

/// <summary>Routes to configured endpoints and falls back from an unhealthy intranet tier to local.</summary>
public sealed class TierRoutingModelGateway(
    IReadOnlyDictionary<ModelEndpointTier, IModelGateway> endpoints,
    ModelPolicy policy,
    ILogger<TierRoutingModelGateway> logger) : IModelGateway
{
    public CapabilityState Capability =>
        endpoints.TryGetValue(policy.DefaultTier, out var gateway)
            ? gateway.Capability
            : CapabilityState.Unavailable(CapabilityKind.ModelGateway, "Default model tier has no endpoint.");

    public string ChatModelId =>
        endpoints.TryGetValue(policy.DefaultTier, out var gateway) ? gateway.ChatModelId : string.Empty;

    public string EmbeddingModelId =>
        endpoints.TryGetValue(policy.DefaultTier, out var gateway) ? gateway.EmbeddingModelId : string.Empty;

    public async Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct)
    {
        try
        {
            return await GetGateway(request.Tier).CompleteAsync(request, ct);
        }
        catch (ModelGatewayException ex) when (request.Tier == ModelEndpointTier.CorporateIntranet && policy.AllowLocalFallback)
        {
            logger.LogWarning("Intranet model unavailable ({ExceptionType}); falling back to local.", ex.GetType().Name);
            var local = GetGateway(ModelEndpointTier.LocalOffline);
            var fallback = await local.CompleteAsync(request with { Tier = ModelEndpointTier.LocalOffline }, ct);
            return fallback with { Tier = ModelEndpointTier.LocalOffline };
        }
    }

    public async Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct)
    {
        try
        {
            return await GetGateway(request.Tier).EmbedAsync(request, ct);
        }
        catch (ModelGatewayException ex) when (request.Tier == ModelEndpointTier.CorporateIntranet && policy.AllowLocalFallback)
        {
            logger.LogWarning("Intranet embedding unavailable ({ExceptionType}); falling back to local.", ex.GetType().Name);
            return await GetGateway(ModelEndpointTier.LocalOffline)
                .EmbedAsync(request with { Tier = ModelEndpointTier.LocalOffline }, ct);
        }
    }

    public Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct) =>
        endpoints.TryGetValue(policy.DefaultTier, out var gateway)
            ? gateway.ProbeAsync(ct)
            : Task.FromResult(new ModelCapabilitySnapshot(
                false,
                false,
                null,
                string.Empty,
                policy.DefaultTier,
                "Default tier endpoint is missing."));

    private IModelGateway GetGateway(ModelEndpointTier tier)
    {
        if (endpoints.TryGetValue(tier, out var gateway))
        {
            return gateway;
        }

        if (tier == ModelEndpointTier.CorporateIntranet && policy.AllowLocalFallback)
        {
            return endpoints[ModelEndpointTier.LocalOffline];
        }

        throw new ModelGatewayException($"No endpoint configured for tier {tier}.");
    }
}

public sealed class EndpointModelGateway(
    HttpModelBackend backend,
    ModelEndpointSettings endpoint,
    ILogger<EndpointModelGateway> logger) : IModelGateway
{
    private volatile ModelCapabilitySnapshot _snapshot = new(
        false,
        false,
        endpoint.ChatModel,
        endpoint.EmbeddingModel,
        endpoint.Tier,
        "Endpoint has not been probed.");

    public CapabilityState Capability => _snapshot.ChatAvailable
        ? CapabilityState.Supported(CapabilityKind.ModelGateway, $"{endpoint.Tier} model ready ({endpoint.ChatModel})")
        : CapabilityState.Degraded(CapabilityKind.ModelGateway, $"{endpoint.Tier}: {_snapshot.Diagnostic}");

    public string ChatModelId => endpoint.ChatModel;
    public string EmbeddingModelId => endpoint.EmbeddingModel;

    public async Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct)
    {
        if (request.Tier != endpoint.Tier)
        {
            throw new ModelGatewayException($"Endpoint tier {endpoint.Tier} cannot serve {request.Tier}.");
        }

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
                ModelId = endpoint.ChatModel,
                Temperature = (float)request.Temperature,
                MaxOutputTokens = request.MaxOutputTokens
            },
            ct);
        return new ModelCompletionResult(
            response.Messages.LastOrDefault()?.Text ?? string.Empty,
            endpoint.ChatModel,
            endpoint.Tier,
            false,
            Stopwatch.GetElapsedTime(started));
    }

    public async Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct)
    {
        if (request.Tier != endpoint.Tier)
        {
            throw new ModelGatewayException($"Endpoint tier {endpoint.Tier} cannot serve {request.Tier}.");
        }

        if (request.Inputs.Count == 0)
        {
            return new ModelEmbeddingResult([], endpoint.EmbeddingModel, 0);
        }

        var result = await backend.GenerateAsync(
            request.Inputs,
            new EmbeddingGenerationOptions { ModelId = endpoint.EmbeddingModel },
            ct);
        var vectors = result.Select(embedding => embedding.Vector).ToArray();
        return new ModelEmbeddingResult(vectors, endpoint.EmbeddingModel, vectors.Length == 0 ? 0 : vectors[0].Length);
    }

    public async Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct)
    {
        var probe = await backend.ProbeAsync(ct);
        _snapshot = new ModelCapabilitySnapshot(
            probe.ChatAvailable,
            probe.EmbeddingsAvailable,
            endpoint.ChatModel,
            endpoint.EmbeddingModel,
            endpoint.Tier,
            probe.Diagnostic);
        if (!probe.ChatAvailable)
        {
            logger.LogInformation("Model endpoint {Tier} is not ready: {Diagnostic}", endpoint.Tier, probe.Diagnostic);
        }

        return _snapshot;
    }
}

/// <summary>Gate 1 minimize, Gate 2 redact, Gate 3 reject residual secrets and sanitize responses.</summary>
public sealed class ThreeGateModelGateway(
    IModelGateway inner,
    ITextRedactor redactor) : IModelGateway
{
    public CapabilityState Capability => inner.Capability;
    public string ChatModelId => inner.ChatModelId;
    public string EmbeddingModelId => inner.EmbeddingModelId;

    public async Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct)
    {
        var system = redactor.Redact(PromptMinimizer.Minimize(request.SystemPrompt, 4_000));
        var user = redactor.Redact(PromptMinimizer.Minimize(request.UserPrompt, 20_000));
        if (ResidualSensitiveDataDetector.HasResidualSensitiveData(system.Text) ||
            ResidualSensitiveDataDetector.HasResidualSensitiveData(user.Text))
        {
            throw new ModelPolicyViolationException("Sensitive data remained after redaction; outbound call blocked.");
        }

        var result = await inner.CompleteAsync(
            request with { SystemPrompt = system.Text, UserPrompt = user.Text },
            ct);
        var output = redactor.Redact(result.Text);
        return result with
        {
            Text = output.Text,
            RedactionApplied = result.RedactionApplied || system.Count + user.Count + output.Count > 0
        };
    }

    public async Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct)
    {
        var inputs = request.Inputs
            .Select(input => redactor.Redact(PromptMinimizer.Minimize(input, 8_000)).Text)
            .ToArray();
        if (inputs.Any(ResidualSensitiveDataDetector.HasResidualSensitiveData))
        {
            throw new ModelPolicyViolationException("Sensitive data remained in embedding input; outbound call blocked.");
        }

        return await inner.EmbedAsync(request with { Inputs = inputs }, ct);
    }

    public Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct) => inner.ProbeAsync(ct);
}

public static partial class PromptMinimizer
{
    public static string Minimize(string? input, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var text = input.Replace("\0", string.Empty, StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
        text = ExcessBlankLinesRegex().Replace(text, "\n\n");
        return text.Length <= maxCharacters ? text : text[..maxCharacters];
    }

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex ExcessBlankLinesRegex();
}

public sealed partial class ResidualSensitiveDataDetector
{
    public static bool HasResidualSensitiveData(string input) =>
        !string.IsNullOrEmpty(input) && ResidualPatterns().Any(pattern => pattern.IsMatch(input));

    private static IEnumerable<Regex> ResidualPatterns()
    {
        yield return PrivateKeyRegex();
        yield return CredentialRegex();
        yield return BearerRegex();
        yield return EmailRegex();
        yield return PhoneRegex();
        yield return LongSecretRegex();
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|api[_-]?key|secret|token)\b\s*[:=]\s*(?!\[REDACTED)(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialRegex();

    [GeneratedRegex(@"(?i)\bBearer\s+(?!\[REDACTED)[A-Za-z0-9._~+/\-]{12,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<!\d)1[3-9]\d{9}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"\b[A-Fa-f0-9]{40,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex LongSecretRegex();
}
