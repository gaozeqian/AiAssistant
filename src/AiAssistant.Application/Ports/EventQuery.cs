using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>
/// Keyset cursor for "older than this row" paging. Unlike an offset, it stays correct when new
/// events are captured between two page loads: freshly written rows sort before the cursor and
/// therefore never shift, duplicate or skip the already-loaded pages.
/// </summary>
public sealed record EventCursor(DateTimeOffset OccurredAtUtc, string EventId);

public sealed record EventQuery(
    DateOnly? LocalDate = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    SourceKind? Source = null,
    string? SearchKeyword = null,
    int Limit = 500,
    DateOnly? FromLocalDate = null,
    DateOnly? ToLocalDate = null,
    bool Descending = false,
    /// <summary>Returns rows strictly older than the cursor; pair it with <see cref="Descending"/>.</summary>
    EventCursor? Before = null);
