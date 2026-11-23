namespace AiAssistant.Host.Commands;

/// <summary>
/// Commands that live only in tools/AiAssistant.Diagnostics.Host. Reject them explicitly instead
/// of silently launching the desktop shell.
/// </summary>
internal sealed class DiagnosticsOnlyCommand : ICliCommand
{
    private static readonly string[] Commands =
    [
        "--benchmark-retrieval", "--benchmark-asr", "--benchmark-ocr", "--benchmark-kws", "--ui-e2e"
    ];

    public bool RequiresDatabaseInitialization => false;

    public bool Matches(string[] args) => args.Any(argument =>
        Commands.Contains(argument, StringComparer.OrdinalIgnoreCase));

    public Task<CliResult> ExecuteAsync(CliCommandContext context, CancellationToken ct)
    {
        Console.Error.WriteLine(
            "This command moved to the developer-only diagnostics host: " +
            "dotnet run --project tools/AiAssistant.Diagnostics.Host -c Release -- " +
            context.Args.First(argument => Commands.Contains(argument, StringComparer.OrdinalIgnoreCase)));
        return Task.FromResult(CliResult.Done(3));
    }
}
