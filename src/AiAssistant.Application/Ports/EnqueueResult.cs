using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record EnqueueResult(string JobId, bool DeduplicatedExistingPendingJob);
