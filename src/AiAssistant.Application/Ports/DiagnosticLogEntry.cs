using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record DiagnosticLogEntry(
    DateTimeOffset TimestampUtc,
    string Level,
    string Category,
    string SanitizedMessage);
