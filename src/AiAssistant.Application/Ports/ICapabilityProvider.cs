using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface ICapabilityProvider
{
    string PlatformName { get; }
    IReadOnlyList<CapabilityState> GetCapabilities();
}
