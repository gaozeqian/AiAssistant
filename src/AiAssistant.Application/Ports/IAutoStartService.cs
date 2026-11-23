using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IAutoStartService
{
    CapabilityState Capability { get; }
    Task<bool> IsEnabledAsync(CancellationToken ct);
    Task SetEnabledAsync(bool enabled, CancellationToken ct);
}
