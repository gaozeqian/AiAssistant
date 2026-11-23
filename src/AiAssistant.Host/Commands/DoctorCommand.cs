using AiAssistant.Application.Diagnostics;

namespace AiAssistant.Host.Commands;

internal sealed class DoctorCommand : ICliCommand
{
    public bool RequiresDatabaseInitialization => false;

    public bool Matches(string[] args) => CliSupport.HasOption(args, "--doctor");

    public async Task<CliResult> ExecuteAsync(CliCommandContext context, CancellationToken ct)
    {
        var runtime = context.Runtime;
        await runtime.Services.DatabaseMaintenance.InitializeAsync(ct);
        var health = await runtime.Services.DatabaseMaintenance.CheckHealthAsync(ct);
        Console.WriteLine(
            $"storage: healthy={health.IsHealthy} schema=v{health.SchemaVersion.Number} " +
            $"events={health.EventCount} media={health.EncryptedBlobCount} integrity={health.IntegrityMessage}");

        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(10));
            var capabilities = await runtime.Services.ModelCapabilityProber.ProbeAllAsync(
                forceRefresh: false,
                probeCts.Token);
            if (capabilities.Count == 0)
            {
                Console.WriteLine("models: none configured");
            }

            foreach (var capability in capabilities)
            {
                Console.WriteLine(
                    $"model {capability.Tier}: chat={capability.ChatAvailable} " +
                    $"embed={capability.EmbeddingsAvailable} json={capability.StructuredOutputAvailable} " +
                    $"tools={capability.ToolCallingAvailable} | {capability.Diagnostic}");
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("models: probe timed out after 10s");
        }

        if (runtime.Services.MacHelperSupervisor is { } macHelper)
        {
            try
            {
                using var helperCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                helperCts.CancelAfter(TimeSpan.FromSeconds(10));
                await macHelper.StartAsync(helperCts.Token);
            }
            catch (OperationCanceledException ex)
            {
                // Helper start timed out; the status line below still reports the supervisor state.
                SuppressedExceptionLog.Trace("Host.Commands.DoctorCommand", ex);
            }

            Console.WriteLine(
                $"macosHelper: status={macHelper.Status} restarts={macHelper.RestartCount} " +
                $"screen={macHelper.Permissions.ScreenRecording} accessibility={macHelper.Permissions.Accessibility} " +
                $"microphone={macHelper.Permissions.Microphone}");
        }

        if (runtime.Services.Capabilities is { } capabilityProvider)
        {
            foreach (var capability in capabilityProvider.GetCapabilities())
            {
                Console.WriteLine($"capability {capability.Kind}: {capability.Status} | {capability.Summary}");
            }
        }

        var mcp = runtime.Services.McpControl.GetSnapshot();
        Console.WriteLine(
            $"mcp: enabled={mcp.Enabled} running={mcp.Running} endpoint={mcp.Endpoint ?? "-"}");
        Console.WriteLine(
            $"pdfFont: {AiAssistant.Export.PdfSharpReportRenderer.ResolvedFontPath ?? "none (vector fallback)"}");
        return CliResult.Done(health.IsHealthy ? 0 : 1);
    }
}
