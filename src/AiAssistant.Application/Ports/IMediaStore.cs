using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IMediaStore
{
    Task<ContentRef> PutAsync(ContentWriteRequest request, CancellationToken ct);
    Task<byte[]?> ReadDecryptedAsync(string contentId, CancellationToken ct);
    Task<int> IncrementRefAsync(string contentId, CancellationToken ct);
    Task<int> DecrementRefAsync(string contentId, CancellationToken ct);
    Task<int> PurgeExpiredAsync(DateTimeOffset nowUtc, CancellationToken ct);

    /// <summary>
    /// Erases the oldest encrypted blobs until the media directory fits inside <paramref name="maxBytes"/>.
    /// Structured events survive; their media reference is cleared exactly like an expired purge.
    /// </summary>
    Task<int> PurgeOverQuotaAsync(long maxBytes, CancellationToken ct);
}
