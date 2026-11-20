using System.Diagnostics;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using Microsoft.Extensions.Logging;
using ActivityEvent = AiAssistant.Core.Domain.ActivityEvent;

namespace AiAssistant.Processing;

public sealed record ProcessingStageOutcome(
    string StageName,
    string StageVersion,
    TimeSpan Duration,
    bool Succeeded,
    string? ErrorType);

public sealed record ProcessingPipelineMetrics(
    long Runs,
    long StageExecutions,
    long StageFailures,
    long StageTimeouts,
    long LastDurationMilliseconds);

public sealed record ProcessingEnvelope(
    EventProcessingRequest Request,
    string? Text,
    string? Transcript,
    double AsrConfidence,
    string? AsrModelVersion,
    bool IsDuplicate,
    IReadOnlyList<SpeakerTurn> Speakers,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<ProcessingStageOutcome> Outcomes)
{
    public ActivityEvent Event => Request.Event;

    public byte[]? ScreenshotPng => Request.ScreenshotPng;

    public AudioFrame? Audio => Request.Audio;

    public static ProcessingEnvelope FromRequest(EventProcessingRequest request) =>
        new(
            request,
            request.Event.ExtractedText,
            null,
            0,
            null,
            false,
            [],
            new Dictionary<string, string>(request.Event.Metadata, StringComparer.Ordinal),
            []);

    public ProcessingEnvelope WithMetadata(string key, string value)
    {
        var metadata = new Dictionary<string, string>(Metadata, StringComparer.Ordinal) { [key] = value };
        return this with { Metadata = metadata };
    }
}

/// <summary>
/// Runs preprocessing stages sequentially with per-stage timeout and failure isolation.
/// A failing or timed-out stage is recorded as metadata; the raw event always remains persistable.
/// </summary>
public sealed class ProcessingPipeline(
    IEnumerable<IProcessingStage<ProcessingEnvelope, ProcessingEnvelope>> stages,
    ILogger<ProcessingPipeline> logger) : IEventEnrichmentPipeline
{
    private readonly IProcessingStage<ProcessingEnvelope, ProcessingEnvelope>[] _stages = stages.ToArray();
    private long _runs;
    private long _stageExecutions;
    private long _stageFailures;
    private long _stageTimeouts;
    private long _lastDurationMilliseconds;

    public IReadOnlyList<string> StageNames => _stages.Select(stage => stage.StageName).ToArray();

    public ProcessingPipelineMetrics GetMetrics() => new(
        Interlocked.Read(ref _runs),
        Interlocked.Read(ref _stageExecutions),
        Interlocked.Read(ref _stageFailures),
        Interlocked.Read(ref _stageTimeouts),
        Interlocked.Read(ref _lastDurationMilliseconds));

    public async Task<ActivityEvent> EnrichAsync(EventProcessingRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var total = Stopwatch.StartNew();
        var envelope = ProcessingEnvelope.FromRequest(request);
        var outcomes = new List<ProcessingStageOutcome>(_stages.Length);

        foreach (var stage in _stages)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _stageExecutions);
            var stageStopwatch = Stopwatch.StartNew();
            using var stageCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (stage.DefaultTimeout > TimeSpan.Zero)
            {
                stageCts.CancelAfter(stage.DefaultTimeout);
            }

            try
            {
                envelope = await stage.ProcessAsync(envelope, stageCts.Token);
                outcomes.Add(new ProcessingStageOutcome(
                    stage.StageName,
                    stage.StageVersion,
                    stageStopwatch.Elapsed,
                    Succeeded: true,
                    ErrorType: null));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _stageTimeouts);
                Interlocked.Increment(ref _stageFailures);
                outcomes.Add(new ProcessingStageOutcome(
                    stage.StageName,
                    stage.StageVersion,
                    stageStopwatch.Elapsed,
                    Succeeded: false,
                    ErrorType: nameof(TimeoutException)));
                logger.LogWarning("Processing stage {StageName} timed out after {TimeoutMs} ms.",
                    stage.StageName,
                    stage.DefaultTimeout.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _stageFailures);
                outcomes.Add(new ProcessingStageOutcome(
                    stage.StageName,
                    stage.StageVersion,
                    stageStopwatch.Elapsed,
                    Succeeded: false,
                    ErrorType: ex.GetType().Name));
                logger.LogWarning("Processing stage {StageName} degraded with {ExceptionType}.",
                    stage.StageName,
                    ex.GetType().Name);
            }
        }

        var metadata = new Dictionary<string, string>(envelope.Metadata, StringComparer.Ordinal)
        {
            ["processing.outcomes"] = string.Join(",", outcomes.Select(o => $"{o.StageName}:{(o.Succeeded ? "ok" : "failed")}")),
            ["processing.failures"] = outcomes.Count(o => !o.Succeeded).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        var extractedText = !string.IsNullOrWhiteSpace(envelope.Transcript)
            ? envelope.Transcript
            : envelope.Text;

        var enriched = envelope.Event with
        {
            ExtractedText = extractedText,
            Metadata = metadata
        };

        total.Stop();
        Interlocked.Increment(ref _runs);
        Interlocked.Exchange(ref _lastDurationMilliseconds, total.ElapsedMilliseconds);
        return enriched;
    }
}
