using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record EventQuery(
    DateOnly? LocalDate = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    SourceKind? Source = null,
    string? SearchKeyword = null,
    int Limit = 500,
    DateOnly? FromLocalDate = null,
    DateOnly? ToLocalDate = null);
