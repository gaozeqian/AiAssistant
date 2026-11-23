using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

public interface IModelAuditSink
{
    void Record(ModelAuditEntry entry);
    IReadOnlyList<ModelAuditEntry> GetRecent(int maxCount = 50);
}
