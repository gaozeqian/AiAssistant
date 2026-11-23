namespace AiAssistant.Host.Commands;

internal sealed class RebuildVectorsCommand : ICliCommand
{
    public bool RequiresDatabaseInitialization => true;

    public bool Matches(string[] args) => CliSupport.HasOption(args, "--rebuild-vectors");

    public async Task<CliResult> ExecuteAsync(CliCommandContext context, CancellationToken ct)
    {
        var indexed = await context.Services.EmbeddingIndexer.RebuildAllAsync(ct);
        Console.WriteLine(
            $"Vector index rebuilt for {indexed} events using model {context.Services.ModelGateway.EmbeddingModelId}.");
        return CliResult.Done();
    }
}

internal sealed class BackupCommand : ICliCommand
{
    public bool RequiresDatabaseInitialization => true;

    public bool Matches(string[] args) => CliSupport.HasOption(args, "--backup");

    public async Task<CliResult> ExecuteAsync(CliCommandContext context, CancellationToken ct)
    {
        CliSupport.TryGetOption(context.Args, "--backup", out var backupPath);
        var fullPath = Path.GetFullPath(backupPath);
        await context.Services.DatabaseMaintenance.BackupEncryptedAsync(fullPath, ct);
        Console.WriteLine($"AiAssistant encrypted backup written to {fullPath}");
        return CliResult.Done();
    }
}

internal sealed class RestoreCommand : ICliCommand
{
    public bool RequiresDatabaseInitialization => true;

    public bool Matches(string[] args) => CliSupport.HasOption(args, "--restore");

    public async Task<CliResult> ExecuteAsync(CliCommandContext context, CancellationToken ct)
    {
        CliSupport.TryGetOption(context.Args, "--restore", out var restorePath);
        if (!CliSupport.HasOption(context.Args, "--confirm-restore"))
        {
            Console.Error.WriteLine("Refusing to restore without --confirm-restore; existing data would be replaced.");
            return CliResult.Done(3);
        }

        var fullPath = Path.GetFullPath(restorePath);
        await context.Services.DatabaseMaintenance.RestoreEncryptedAsync(fullPath, ct);
        Console.WriteLine($"AiAssistant encrypted backup restored from {fullPath}");
        return CliResult.Done();
    }
}

internal sealed class PurgeDataCommand : ICliCommand
{
    public bool RequiresDatabaseInitialization => true;

    public bool Matches(string[] args) => CliSupport.HasOption(args, "--purge-data");

    public async Task<CliResult> ExecuteAsync(CliCommandContext context, CancellationToken ct)
    {
        if (!CliSupport.HasOption(context.Args, "--confirm-purge"))
        {
            Console.Error.WriteLine("Refusing to delete all local data without --confirm-purge.");
            return CliResult.Done(3);
        }

        var deleted = await context.Services.DataDeletion.PurgeAllAsync(ct);
        Console.WriteLine(
            $"AiAssistant local data purged: events={deleted.EventsDeleted} media={deleted.MediaFilesDeleted} " +
            $"reports={deleted.ReportsDeleted} jobs={deleted.JobsDeleted} voice={deleted.VoiceCommandsDeleted}");
        return CliResult.Done();
    }
}
