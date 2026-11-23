namespace AiAssistant.Host.Commands;

/// <summary>One headless CLI command. Implementations keep their own matching rule and exit-code contract.</summary>
internal interface ICliCommand
{
    /// <summary>Maintenance commands must initialize the encrypted schema before touching storage.</summary>
    bool RequiresDatabaseInitialization { get; }

    bool Matches(string[] args);

    Task<CliResult> ExecuteAsync(CliCommandContext context, CancellationToken ct);
}
