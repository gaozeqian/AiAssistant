using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface ITextRedactor
{
    TextRedaction Redact(string input);
}
