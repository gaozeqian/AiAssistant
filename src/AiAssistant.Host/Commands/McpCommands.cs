using AiAssistant.Application.Diagnostics;

namespace AiAssistant.Host.Commands;

internal sealed class McpTokenCommand : ICliCommand
{
    public bool RequiresDatabaseInitialization => false;

    public bool Matches(string[] args) => CliSupport.HasOption(args, "--mcp-token");

    public async Task<CliResult> ExecuteAsync(CliCommandContext context, CancellationToken ct)
    {
        // Explicit local action: prints the bearer token so the user can paste it into an MCP client.
        Console.WriteLine(await context.Services.McpControl.RevealTokenAsync(ct));
        return CliResult.Done();
    }
}

internal sealed class McpServeCommand : ICliCommand
{
    public bool RequiresDatabaseInitialization => false;

    public bool Matches(string[] args) => CliSupport.HasOption(args, "--mcp-serve");

    public async Task<CliResult> ExecuteAsync(CliCommandContext context, CancellationToken ct)
    {
        await context.Runtime.StartMcpOnlyAsync(ct);
        var mcpStatus = context.Services.McpControl.GetSnapshot();
        if (!mcpStatus.Running || mcpStatus.Endpoint is null)
        {
            Console.Error.WriteLine("Local MCP is disabled. Set AIASSISTANT_MCP_ENABLED=true to enable it.");
            return CliResult.Done(3);
        }

        Console.WriteLine($"Local read-only MCP listening on {mcpStatus.Endpoint}");
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
        }
        catch (OperationCanceledException ex)
        {
            // Normal Ctrl+C shutdown.
            SuppressedExceptionLog.Trace("Host.Commands.McpServeCommand", ex);
        }

        return CliResult.Done();
    }
}
