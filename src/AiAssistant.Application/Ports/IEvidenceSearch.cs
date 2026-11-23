using AiAssistant.Core.Domain;

namespace AiAssistant.Application.Ports;

/// <summary>Hybrid keyword + vector retrieval over local events, always time-filtered.</summary>
public interface IEvidenceSearch
{
    Task<SearchQueryResult> SearchAsync(
        string query,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken ct);
}
