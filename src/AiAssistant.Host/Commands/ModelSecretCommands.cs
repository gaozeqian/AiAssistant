namespace AiAssistant.Host.Commands;

internal sealed class ImportModelSecretCommand : ICliCommand
{
    public bool RequiresDatabaseInitialization => false;

    public bool Matches(string[] args) => CliSupport.HasOption(args, "--import-model-secret");

    public async Task<CliResult> ExecuteAsync(CliCommandContext context, CancellationToken ct)
    {
        CliSupport.TryGetOption(context.Args, "--import-model-secret", out var secretName);
        Console.Error.Write($"Secret value for '{secretName}': ");
        var secretValue = CliSupport.ReadSecretFromConsole();
        if (string.IsNullOrWhiteSpace(secretValue))
        {
            Console.Error.WriteLine("Secret value is empty; nothing was stored.");
            return CliResult.Done(3);
        }

        await context.Services.ModelSecretAdmin.SetAsync(secretName, secretValue, ct);
        Console.WriteLine($"Secret '{secretName}' stored encrypted.");
        return CliResult.Done();
    }
}

internal sealed class ListModelSecretsCommand : ICliCommand
{
    public bool RequiresDatabaseInitialization => false;

    public bool Matches(string[] args) => CliSupport.HasOption(args, "--list-model-secrets");

    public async Task<CliResult> ExecuteAsync(CliCommandContext context, CancellationToken ct)
    {
        var names = await context.Services.ModelSecretAdmin.ListNamesAsync(ct);
        if (names.Count == 0)
        {
            Console.WriteLine("No model secrets stored.");
        }

        foreach (var name in names)
        {
            Console.WriteLine(name);
        }

        return CliResult.Done();
    }
}

internal sealed class DeleteModelSecretCommand : ICliCommand
{
    public bool RequiresDatabaseInitialization => false;

    public bool Matches(string[] args) => CliSupport.HasOption(args, "--delete-model-secret");

    public async Task<CliResult> ExecuteAsync(CliCommandContext context, CancellationToken ct)
    {
        CliSupport.TryGetOption(context.Args, "--delete-model-secret", out var secretName);
        if (!CliSupport.HasOption(context.Args, "--confirm-delete-secret"))
        {
            Console.Error.WriteLine("Refusing to delete a model secret without --confirm-delete-secret.");
            return CliResult.Done(3);
        }

        var deleted = await context.Services.ModelSecretAdmin.DeleteAsync(secretName, ct);
        Console.WriteLine(deleted
            ? $"Secret '{secretName}' deleted."
            : $"Secret '{secretName}' not found.");
        return CliResult.Done(deleted ? 0 : 1);
    }
}
