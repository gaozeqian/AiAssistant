namespace AiAssistant.Application.Ports;

/// <summary>Renders a <see cref="ReportDocument"/> to the non-text export formats.</summary>
public interface IReportDocumentRenderer
{
    string RenderSvg(ReportDocument document);

    byte[] RenderPng(ReportDocument document);

    byte[] RenderPdf(ReportDocument document);
}
