using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record ModelEmbeddingResult(
    IReadOnlyList<ReadOnlyMemory<float>> Vectors,
    string ModelId,
    int Dimensions);
