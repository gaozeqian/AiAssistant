using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IVectorIndex
{
    Task<VectorIndexMetadata> GetMetadataAsync(CancellationToken ct);
    Task UpsertAsync(string eventId, ReadOnlyMemory<float> embedding, VectorModelDescriptor model, CancellationToken ct);
    Task<IReadOnlyList<VectorSearchHit>> SearchAsync(ReadOnlyMemory<float> queryEmbedding, int topK, CancellationToken ct);
    Task RebuildAsync(
        VectorModelDescriptor targetModel,
        Func<CancellationToken, IAsyncEnumerable<(string EventId, ReadOnlyMemory<float> Embedding)>> generator,
        CancellationToken ct);
}
