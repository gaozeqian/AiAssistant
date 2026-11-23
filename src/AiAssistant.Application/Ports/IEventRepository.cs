using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IEventRepository
{
    Task AppendBatchAsync(IReadOnlyList<ActivityEvent> events, CancellationToken ct);

    /// <summary>
    /// Atomically persists events and their encrypted media blobs so crash/interruption never leaves dangling references.
    /// </summary>
    Task AppendWithMediaBatchAsync(IReadOnlyList<EventWithMediaWrite> items, CancellationToken ct);

    IAsyncEnumerable<ActivityEvent> QueryAsync(EventQuery query, CancellationToken ct);

    Task<ActivityEvent?> GetByIdAsync(string eventId, CancellationToken ct);

    Task<IReadOnlyList<DateOnly>> ListActiveDatesAsync(CancellationToken ct);

    Task<bool> DeleteEventAsync(string eventId, CancellationToken ct);

    Task<int> PurgeDateAsync(DateOnly localDate, CancellationToken ct);
}
