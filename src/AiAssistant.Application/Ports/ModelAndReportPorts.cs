using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record ModelCompletionRequest(
    string SystemPrompt,
    string UserPrompt,
    ModelEndpointTier Tier = ModelEndpointTier.LocalOffline,
    string Purpose = "daily-report",
    double Temperature = 0.2,
    int MaxOutputTokens = 2048);

public sealed record ModelCompletionResult(
    string Text,
    string ModelId,
    ModelEndpointTier Tier,
    bool RedactionApplied,
    TimeSpan Duration);

public sealed record ModelEmbeddingRequest(
    IReadOnlyList<string> Inputs,
    ModelEndpointTier Tier = ModelEndpointTier.LocalOffline,
    string Purpose = "event-index");

public sealed record ModelEmbeddingResult(
    IReadOnlyList<ReadOnlyMemory<float>> Vectors,
    string ModelId,
    int Dimensions);

public sealed record ModelCapabilitySnapshot(
    bool ChatAvailable,
    bool EmbeddingsAvailable,
    string? ChatModel,
    string EmbeddingModel,
    ModelEndpointTier Tier,
    string Diagnostic,
    bool StreamingAvailable = false,
    bool StructuredOutputAvailable = false,
    bool ToolCallingAvailable = false);

public sealed record ModelEndpointCapability(
    ModelEndpointTier Tier,
    string BaseUrl,
    string ChatModel,
    string EmbeddingModel,
    bool ChatAvailable,
    bool StreamingAvailable,
    bool EmbeddingsAvailable,
    bool StructuredOutputAvailable,
    bool ToolCallingAvailable,
    string Diagnostic,
    DateTimeOffset ProbedAtUtc);

public interface IModelCapabilityProber
{
    Task<IReadOnlyList<ModelEndpointCapability>> ProbeAllAsync(bool forceRefresh, CancellationToken ct);
}

/// <summary>Single model boundary for chat and embeddings; all outbound calls pass through redaction and audit.</summary>
public interface IModelGateway
{
    CapabilityState Capability { get; }
    string ChatModelId { get; }
    string EmbeddingModelId { get; }

    Task<ModelCompletionResult> CompleteAsync(ModelCompletionRequest request, CancellationToken ct);
    Task<ModelEmbeddingResult> EmbedAsync(ModelEmbeddingRequest request, CancellationToken ct);
    Task<ModelCapabilitySnapshot> ProbeAsync(CancellationToken ct);
}

public sealed record ModelAuditEntry(
    DateTimeOffset TimestampUtc,
    string Purpose,
    ModelEndpointTier Tier,
    string ModelId,
    int PromptCharacters,
    int ResponseCharacters,
    bool RedactionApplied,
    bool Succeeded,
    string? ErrorType,
    long DurationMilliseconds);

public interface IModelAuditSink
{
    void Record(ModelAuditEntry entry);
    IReadOnlyList<ModelAuditEntry> GetRecent(int maxCount = 50);
}

public sealed record TextRedaction(string Text, int Count);

public interface ITextRedactor
{
    TextRedaction Redact(string input);
}

public sealed record ReportGenerationOutcome(
    DailyReport? Report,
    bool SkippedExisting,
    string? FailureReason)
{
    public bool Succeeded => Report is not null && FailureReason is null;
}

/// <summary>Generates and persists a versioned report; implemented by Summarization, invoked by Scheduler.</summary>
public interface IReportGenerationRunner
{
    Task<ReportGenerationOutcome> GenerateAsync(ReportJobRequest request, CancellationToken ct);
}

public interface IReportRepository
{
    Task<DailyReport?> GetLatestAsync(DateOnly localDate, CancellationToken ct);
    Task<IReadOnlyList<DailyReport>> ListVersionsAsync(DateOnly localDate, CancellationToken ct);

    Task<DailyReport> AppendAsync(
        DateOnly localDate,
        string markdown,
        string statsJson,
        string modelId,
        ModelEndpointTier endpointTier,
        bool redactionApplied,
        ReportTriggerSource triggerSource,
        DateTimeOffset generatedAtUtc,
        CancellationToken ct);
}
