

namespace AiAssistant.Application.Ports;

public sealed record LocalMcpSnapshot(
    bool Enabled,
    bool Running,
    int Port,
    string? Endpoint,
    long RequestCount,
    long UnauthorizedCount,
    long RateLimitedCount,
    long ToolCallCount,
    bool TokenAvailable)
{
    public static LocalMcpSnapshot Disabled { get; } =
        new(false, false, 0, null, 0, 0, 0, 0, false);
}
