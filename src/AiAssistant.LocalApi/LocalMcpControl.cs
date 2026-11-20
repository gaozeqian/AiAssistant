using AiAssistant.Application.Ports;

namespace AiAssistant.LocalApi;

/// <summary>Diagnostics surface for the opt-in MCP endpoint: status, token reveal and token rotation.</summary>
public sealed class LocalMcpControl(
    LoopbackMcpServer server,
    LocalMcpTokenStore tokens,
    McpServerOptions options) : ILocalMcpControl
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
}
