using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IVoiceViewNavigator
{
    void ShowTimeline();
    void ShowReports();
    void RequestExit();
}
