using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IModelCapabilityProber
{
    Task<IReadOnlyList<ModelEndpointCapability>> ProbeAllAsync(bool forceRefresh, CancellationToken ct);
}
