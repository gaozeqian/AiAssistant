using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record ExtractedTextResult(
    string Text,
    TextExtractionMethod Method,
    double Confidence);
