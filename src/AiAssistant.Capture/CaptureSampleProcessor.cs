using AiAssistant.Application.Policies;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Capture;

internal enum CaptureSampleOutcome
{
    Persisted,
    SkippedBackfill,
    SkippedBlacklist,
    CoalescedDuplicate,
    Failed
}

/// <summary>
/// Single-sample processing: resume cutoff, privacy blacklist, pHash/window deduplication,
/// best-effort text extraction/enrichment, then encrypted persistence.
/// </summary>
internal sealed class CaptureSampleProcessor(
    ITextExtractor textExtractor,
    IEventRepository eventRepository,
    PrivacyBlacklistPolicy blacklistPolicy,
    CapturePipelineOptions options,
    ILogger<BoundedCaptureCoordinator> logger,
    IEventEnrichmentPipeline? enrichmentPipeline)
{
    private ulong? _lastPersistedPhash;
    private string? _lastPersistedWindowKey;
    private DateTimeOffset _lastPersistedAtUtc = DateTimeOffset.MinValue;

    public async Task<CaptureSampleOutcome> ProcessAsync(
        CapturedSample sample,
        DateTimeOffset resumeCutoffUtc,
        CancellationToken ct)
    {
        if (sample.Timestamp.OccurredAtUtc < resumeCutoffUtc)
        {
            // Do not backfill events from before resume.
            return CaptureSampleOutcome.SkippedBackfill;
        }

        if (blacklistPolicy.IsExcluded(sample.AppName, sample.WindowTitle))
        {
            return CaptureSampleOutcome.SkippedBlacklist;
        }

        var windowKey = $"{sample.AppName}\u001F{sample.WindowTitle}";
        if (sample.PerceptualHash.HasValue &&
            _lastPersistedPhash.HasValue &&
            string.Equals(windowKey, _lastPersistedWindowKey, StringComparison.Ordinal) &&
            (sample.Timestamp.OccurredAtUtc - _lastPersistedAtUtc) < options.DuplicateCoalesceWindow &&
            PerceptualHash.IsNearDuplicate(sample.PerceptualHash.Value, _lastPersistedPhash.Value))
        {
            return CaptureSampleOutcome.CoalescedDuplicate;
        }

        var extractedText = sample.ExtractedText;
        if (string.IsNullOrWhiteSpace(extractedText) && sample.Source is SourceKind.WindowActivity or SourceKind.ScreenFrame)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(options.TextExtractionTimeout);
                var result = await textExtractor.ExtractAsync(
                    new TextExtractionRequest(sample.NativeWindowHandle, sample.AppName, sample.WindowTitle, sample.RawMediaBytes),
                    timeoutCts.Token);
                extractedText = result.Text;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Preprocessing/OCR failure must never block raw event persistence.
                logger.LogDebug("Text extraction degraded with {ExceptionType}; saving raw event.", ex.GetType().Name);
            }
        }

        var domainEvent = new ActivityEvent(
            StableId.NewEventId(sample.Timestamp.OccurredAtUtc),
            sample.Timestamp,
            sample.Source,
            sample.AppName,
            sample.WindowTitle,
            extractedText: extractedText,
            perceptualHash: sample.PerceptualHash,
            metadata: sample.Metadata);

        if (enrichmentPipeline is not null)
        {
            try
            {
                domainEvent = await enrichmentPipeline.EnrichAsync(
                    new EventProcessingRequest(
                        domainEvent,
                        sample.RawMediaBytes,
                        Audio: null,
                        NativeWindowHandle: sample.NativeWindowHandle),
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Preprocessing is best-effort; the original raw event must still be persisted.
                logger.LogWarning(
                    "Event enrichment degraded with {ExceptionType}; persisting raw event.",
                    ex.GetType().Name);
            }
        }

        try
        {
            await eventRepository.AppendWithMediaBatchAsync(
            [
                new EventWithMediaWrite(domainEvent, sample.RawMediaBytes, sample.MediaKind)
            ], ct);

            _lastPersistedPhash = sample.PerceptualHash;
            _lastPersistedWindowKey = windowKey;
            _lastPersistedAtUtc = sample.Timestamp.OccurredAtUtc;
            return CaptureSampleOutcome.Persisted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "Failed persisting event of source {SourceKind} due to {ExceptionType}.",
                sample.Source,
                ex.GetType().Name);
            return CaptureSampleOutcome.Failed;
        }
    }
}
