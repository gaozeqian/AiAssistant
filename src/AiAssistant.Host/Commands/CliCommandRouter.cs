namespace AiAssistant.Host.Commands;

/// <summary>
/// Ordered command table. Earlier entries win, matching the historical if-waterfall precedence;
/// <see cref="StartupCommand"/> is the terminal fallback for capture-smoke, startup-check and the GUI.
/// </summary>
internal static class CliCommandRouter
{
    private static readonly ICliCommand[] Commands =
    [
        new McpTokenCommand(),
        new McpServeCommand(),
        new DoctorCommand(),
        new ImportModelSecretCommand(),
        new ListModelSecretsCommand(),
        new DeleteModelSecretCommand(),
        new ProbeModelCommand(),
        new BenchmarkModelCommand(),
        new ReportRangeCommand(),
        new AskCommand(),
        new RebuildVectorsCommand(),
        new BackupCommand(),
        new RestoreCommand(),
        new PurgeDataCommand(),
        new DiagnosticsOnlyCommand(),
        new StartupCommand()
    ];

    public static async Task<CliResult> ExecuteAsync(AppRuntime runtime, string[] args, CancellationToken ct)
    {
        var command = Commands.First(candidate => candidate.Matches(args));
        try
        {
            if (command.RequiresDatabaseInitialization)
            {
                await runtime.Services.DatabaseMaintenance.InitializeAsync(ct);
            }

            return await command.ExecuteAsync(new CliCommandContext(runtime, args), ct);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"AiAssistant usage error: {ex.Message}");
            return CliResult.Done(3);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AiAssistant startup failed ({ex.GetType().Name}): {ex.Message}");
            return CliResult.Done(2);
        }
    }
}
