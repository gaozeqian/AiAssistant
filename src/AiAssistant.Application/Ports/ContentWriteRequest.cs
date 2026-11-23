using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record ContentWriteRequest(
    string ContentId,
    ContentKind Kind,
    ReadOnlyMemory<byte> Plaintext,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc = null,
    ulong? PerceptualHash = null);
