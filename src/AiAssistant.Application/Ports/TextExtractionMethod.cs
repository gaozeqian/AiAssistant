using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public enum TextExtractionMethod
{
    None = 0,
    AccessibilityTree = 1,
    SystemOcr = 2
}
