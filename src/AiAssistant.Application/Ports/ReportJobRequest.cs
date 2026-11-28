using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public sealed record ReportJobRequest(
    DateOnly TargetDate,
    ReportTriggerSource TriggerSource,
    bool ForceOverwrite = false,
    ReportJobKind Kind = ReportJobKind.Daily);
