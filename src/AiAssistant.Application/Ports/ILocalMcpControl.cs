

namespace AiAssistant.Application.Ports;

/// <summary>Diagnostics-facing control of the opt-in read-only local MCP endpoint.</summary>
public interface ILocalMcpControl
{
    LocalMcpSnapshot GetSnapshot();

    Task<string> RevealTokenAsync(CancellationToken ct);

    Task<string> RotateTokenAsync(CancellationToken ct);

    /// <summary>Persists the opt-in flag and starts or stops the loopback listener accordingly.</summary>
    Task SetEnabledAsync(bool enabled, CancellationToken ct);
}
