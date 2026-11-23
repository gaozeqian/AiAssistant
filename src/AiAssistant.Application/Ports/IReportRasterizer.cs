using AiAssistant.Core.Domain;

namespace AiAssistant.Application.Ports;

/// <summary>Optional PNG/PDF rasterization for range reports; implemented outside the dependency-free Application layer.</summary>
public interface IReportRasterizer
{
    byte[] RenderPng(RangeReport report);

    byte[] RenderPdf(RangeReport report);
}
