using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Capture;

/// <summary>Persists pause/resume control audit events with bounded retries.</summary>
internal sealed class CaptureControlAuditWriter(
    IEventRepository eventRepository,
    IClock clock,
    ILogger<BoundedCaptureCoordinator> logger)
{
    public async Task<bool> PersistAsync(string actionName, CancellationToken ct)
    {
        var now = clock.Now();
        var controlEvent = new ActivityEvent(
            StableId.NewEventId(now.OccurredAtUtc),
            now,
            SourceKind.SystemControl,
            "AiAssistant",
            actionName,
            summary: actionName,
            privacy: PrivacyLevel.LocalOnly);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await eventRepository.AppendBatchAsync([controlEvent], ct);
                logger.LogInformation("Control audit event {ControlAction} persisted.", actionName);
                return true;
            }
            catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Retrying control event {ControlAction} after {ExceptionType} (attempt {Attempt}).",
                    actionName,
                    ex.GetType().Name,
                    attempt);
                await Task.Delay(attempt * 15, ct);
            }
            catch (Exception ex)
            {
                logger.LogError("Failed to persist control event {ControlAction}: {ExceptionType}.", actionName, ex.GetType().Name);
            }
        }

        return false;
    }
}
