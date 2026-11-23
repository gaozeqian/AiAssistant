using System.Globalization;

namespace AiAssistant.Host.Commands;

/// <summary>
/// Terminal fallback: starts the full runtime, then handles capture-smoke / startup-check,
/// or hands control back to <see cref="Program"/> for the Avalonia/AppKit main-thread handoff.
/// </summary>
internal sealed class StartupCommand : ICliCommand
{
    public bool RequiresDatabaseInitialization => false;

    public bool Matches(string[] args) => true;

    public async Task<CliResult> ExecuteAsync(CliCommandContext context, CancellationToken ct)
    {
        var runtime = context.Runtime;
        await runtime.StartAsync(ct);
        if (CliSupport.TryGetOption(context.Args, "--capture-smoke", out var captureSecondsText))
        {
            // Real-machine acceptance helper (S3-R1): runs the capture pipeline headless for N seconds
            // and reports what actually reached the encrypted store, without starting the desktop shell.
            var captureSeconds = int.TryParse(
                captureSecondsText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedSeconds)
                ? Math.Clamp(parsedSeconds, 5, 300)
                : 20;
            await Task.Delay(TimeSpan.FromSeconds(captureSeconds), ct);
            var captureHealth = await runtime.Services.DatabaseMaintenance.CheckHealthAsync(ct);
            Console.WriteLine(
                $"capture-smoke: seconds={captureSeconds} events={captureHealth.EventCount} " +
                $"media={captureHealth.EncryptedBlobCount} healthy={captureHealth.IsHealthy}");
            return CliResult.Done(captureHealth.EventCount > 0 ? 0 : 1);
        }

        if (CliSupport.HasOption(context.Args, "--startup-check"))
        {
            Console.WriteLine($"AiAssistant.Host startup-check OK; recovered jobs: {runtime.RecoveredJobCount}");
            return CliResult.Done();
        }

        // AppKit/Avalonia require the process main thread; Program starts the shell there.
        return CliResult.Desktop();
    }
}
