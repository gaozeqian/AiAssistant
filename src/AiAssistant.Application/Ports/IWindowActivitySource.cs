using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IWindowActivitySource : ICaptureSource
{
    Task<CapturedSample?> GetCurrentForegroundAsync(CancellationToken ct);
}
