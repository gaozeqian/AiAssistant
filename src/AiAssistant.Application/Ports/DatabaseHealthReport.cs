using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record DatabaseHealthReport(
    bool IsHealthy,
    SchemaVersion SchemaVersion,
    string CipherDescription,
    string VectorBackend,
    long EventCount,
    long EncryptedBlobCount,
    string IntegrityMessage);
