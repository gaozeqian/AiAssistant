using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record CapturePipelineMetrics(
    bool IsPaused,
    int QueuedDataItems,
    long PersistedEvents,
    long CoalescedDuplicateFrames,
    long DroppedByBlacklist,
    long DroppedByOverload,
    long ControlCommandsProcessed);
