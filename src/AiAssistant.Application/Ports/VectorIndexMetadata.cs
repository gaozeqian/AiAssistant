using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record VectorIndexMetadata(
    string BackendName,
    bool IsNativeSqliteVecLoaded,
    VectorModelDescriptor? ActiveModel,
    long IndexedCount);
