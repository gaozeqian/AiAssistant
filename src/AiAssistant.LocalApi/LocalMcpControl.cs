using AiAssistant.Application.Ports;

namespace AiAssistant.LocalApi;

/// <summary>Diagnostics surface for the opt-in MCP endpoint: status, token reveal, token rotation and enable toggle.</summary>
public sealed class LocalMcpControl(
    LoopbackMcpServer server,
    LocalMcpTokenStore tokens,
    McpServerOptions options,
    McpEnabledStore store) : ILocalMcpControl
{
    public LocalMcpSnapshot GetSnapshot()
    {
        var snapshot = server.Status;
        return options.Enabled ? snapshot : snapshot with { Enabled = false, Running = false };
    }

    public Task<string> RevealTokenAsync(CancellationToken ct) => tokens.GetOrCreateAsync(ct);

    /// <summary>Stops the listener, replaces the token and restarts only if it was already running.</summary>
    public async Task<string> RotateTokenAsync(CancellationToken ct)
    {
        var wasRunning = server.IsRunning;
        await server.StopAsync(ct);
        var token = await tokens.RotateAsync(ct);
        if (wasRunning)
        {
            await server.StartAsync(ct);
        }

        return token;
    }

    /// <summary>Persists the opt-in flag and starts or stops the loopback listener to match.</summary>
    public async Task SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        options.Enabled = enabled;
        await store.SaveAsync(enabled, ct);
        if (enabled)
        {
            await server.StartAsync(ct);
        }
        else
        {
            await server.StopAsync(ct);
        }
    }
}
