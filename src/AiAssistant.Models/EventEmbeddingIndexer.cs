using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;

namespace AiAssistant.Models;

/// <summary>
/// Embeds captured events through the model gateway and writes versioned vectors through IVectorIndex.
/// No model call is made at startup; indexing is explicit and replaceable.
/// </summary>
public sealed class EventEmbeddingIndexer(
    IEventRepository events,
    IVectorIndex vectorIndex,
    IModelGateway model,
    ModelEndpointTier tier = ModelEndpointTier.LocalOffline,
    string documentStyle = "default")
{
    private const string ModelVersion = "1.0";
    private const int MaxIndexTextLength = 800;
    private const int BatchSize = 24;

    public async Task<int> IndexDateAsync(DateOnly localDate, CancellationToken ct)
    {
        var items = new List<ActivityEvent>();
        await foreach (var item in events.QueryAsync(new EventQuery(LocalDate: localDate, Limit: 10_000), ct))
        {
            items.Add(item);
        }

        return await IndexAsync(items, ct);
    }

    public async Task<int> RebuildAllAsync(CancellationToken ct)
    {
        var all = new List<ActivityEvent>();
        await foreach (var item in events.QueryAsync(new EventQuery(Limit: 10_000), ct))
        {
            all.Add(item);
        }

        if (all.Count == 0)
        {
            return 0;
        }

        var first = await model.EmbedAsync(
            new ModelEmbeddingRequest([BuildText(all[0])], tier, Purpose: "event-index"),
            ct);
        if (first.Vectors.Count == 0 || first.Dimensions == 0)
        {
            return 0;
        }

        var descriptor = new VectorModelDescriptor(model.EmbeddingModelId, IndexVersion, first.Dimensions);
        await vectorIndex.RebuildAsync(
            descriptor,
            token => GenerateBatchesAsync(all, descriptor, token),
            ct);
        return all.Count;
    }

    private async Task<int> IndexAsync(List<ActivityEvent> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return 0;
        }

        var indexed = 0;
        foreach (var batch in items.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var result = await model.EmbedAsync(
                new ModelEmbeddingRequest(batch.Select(BuildText).ToArray(), tier, Purpose: "event-index"),
                ct);
            if (result.Vectors.Count != batch.Length || result.Dimensions == 0)
            {
                throw new InvalidOperationException("Embedding response did not match the requested batch.");
            }

            var descriptor = new VectorModelDescriptor(model.EmbeddingModelId, IndexVersion, result.Dimensions);
            for (var i = 0; i < batch.Length; i++)
            {
                await vectorIndex.UpsertAsync(batch[i].Id, result.Vectors[i], descriptor, ct);
                indexed++;
            }
        }

        return indexed;
    }

    private async IAsyncEnumerable<(string EventId, ReadOnlyMemory<float> Embedding)> GenerateBatchesAsync(
        List<ActivityEvent> items,
        VectorModelDescriptor descriptor,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var batch in items.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var result = await model.EmbedAsync(
                new ModelEmbeddingRequest(batch.Select(BuildText).ToArray(), tier, Purpose: "event-index-rebuild"),
                ct);
            if (result.Dimensions != descriptor.Dimensions)
            {
                throw new InvalidOperationException("Embedding dimensions changed during rebuild.");
            }

            for (var i = 0; i < batch.Length; i++)
            {
                yield return (batch[i].Id, result.Vectors[i]);
            }
        }
    }

    private string IndexVersion =>
        documentStyle.Equals("title-text", StringComparison.OrdinalIgnoreCase)
            ? "1.1-title-text"
            : ModelVersion;

    private string BuildText(ActivityEvent item)
    {
        if (documentStyle.Equals("title-text", StringComparison.OrdinalIgnoreCase))
        {
            var structured = $"title: {item.WindowTitle} | text: {item.Summary ?? item.ExtractedText}";
            return structured.Length <= MaxIndexTextLength ? structured : structured[..MaxIndexTextLength];
        }

        var text = $"{item.AppName} | {item.WindowTitle} | {item.Summary ?? item.ExtractedText}";
        return text.Length <= MaxIndexTextLength ? text : text[..MaxIndexTextLength];
    }
}
