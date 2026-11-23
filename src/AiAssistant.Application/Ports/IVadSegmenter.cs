using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>Voice activity segmentation over the shared AudioHub stream.</summary>
public interface IVadSegmenter : IDisposable
{
    CapabilityState Capability { get; }
    IReadOnlyList<VadSegment> Accept(AudioFrame frame);
    IReadOnlyList<VadSegment> Flush();
}
