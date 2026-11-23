using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public enum VoiceSessionState
{
    Sleeping = 0,
    Listening = 1,
    Processing = 2,
    Responding = 3
}
