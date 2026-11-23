using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record SpeechSynthesisRequest(string Text, string Language = "zh");
