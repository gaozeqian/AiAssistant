using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IReportJobQueue
{
    Task<EnqueueResult> EnqueueAsync(ReportJobRequest request, CancellationToken ct);
    Task<int> RecoverInterruptedJobsAsync(CancellationToken ct);
    Task<ReportJob?> ClaimNextPendingAsync(CancellationToken ct);
    Task UpdateAsync(ReportJob job, CancellationToken ct);
    Task<IReadOnlyList<ReportJob>> ListRecentJobsAsync(int limit, CancellationToken ct);
}
