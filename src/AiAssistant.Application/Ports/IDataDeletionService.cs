using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>User-initiated local data deletion. Never touches the OS key store or model configuration.</summary>
public interface IDataDeletionService
{
    /// <summary>Deletes events, their encrypted media, reports and queued jobs for one local date.</summary>
    Task<DataDeletionResult> PurgeDateAsync(DateOnly localDate, CancellationToken ct);

    /// <summary>Deletes all captured events, media, reports, jobs, voice logs and vector rows.</summary>
    Task<DataDeletionResult> PurgeAllAsync(CancellationToken ct);
}
