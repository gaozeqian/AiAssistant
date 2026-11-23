using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IEventEnrichmentPipeline
{
    Task<ActivityEvent> EnrichAsync(EventProcessingRequest request, CancellationToken ct);
}
