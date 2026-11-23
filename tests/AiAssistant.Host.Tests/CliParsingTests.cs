using AiAssistant.Host;
using AiAssistant.Host.Commands;

namespace AiAssistant.Host.Tests;

[Trait("Category", "Unit")]
public sealed class CliParsingTests
{
    [Fact]
    public void HasOption_IsCaseInsensitiveAndTokenExact()
    {
        var args = new[] { "--doctor", "--Backup", "x" };

        Assert.True(CliSupport.HasOption(args, "--doctor"));
        Assert.True(CliSupport.HasOption(args, "--backup"));
        Assert.False(CliSupport.HasOption(args, "--back"));
    }

    [Fact]
    public void TryGetOption_RequiresAValue()
    {
        Assert.True(CliSupport.TryGetOption(["--ask", "question"], "--ask", out var value));
        Assert.Equal("question", value);

        var ex = Assert.Throws<ArgumentException>(() =>
            CliSupport.TryGetOption(["--ask"], "--ask", out _));
        Assert.Contains("requires a value", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.False(CliSupport.TryGetOption(["--ask"], "--missing", out _));
    }

    [Fact]
    public void ParseDateOption_RejectsNonIsoDates()
    {
        var fallback = new DateOnly(2026, 11, 23);
        Assert.Equal(
            new DateOnly(2026, 1, 2),
            CliSupport.ParseDateOption(["--from", "2026-01-02"], "--from", fallback));
        Assert.Throws<ArgumentException>(() =>
            CliSupport.ParseDateOption(["--from", "01/02/2026"], "--from", fallback));
    }

    [Fact]
    public void ParseIntOption_ClampsAndRejectsText()
    {
        Assert.Equal(366, CliSupport.ParseIntOption(["--days", "9999"], "--days", 7, 1, 366));
        Assert.Equal(7, CliSupport.ParseIntOption([], "--days", 7, 1, 366));
        Assert.Throws<ArgumentException>(() =>
            CliSupport.ParseIntOption(["--days", "abc"], "--days", 7, 1, 366));
    }

    [Fact]
    public void GuiInvocation_IsFalseForEveryHeadlessCommand()
    {
        Assert.True(CliSupport.IsGuiInvocation([]));
        Assert.True(CliSupport.IsGuiInvocation(["--show-window"]));
        Assert.False(CliSupport.IsGuiInvocation(["--doctor"]));
        Assert.False(CliSupport.IsGuiInvocation(["--mcp-serve"]));
        Assert.False(CliSupport.IsGuiInvocation(["--backup", "file.bak"]));
    }

    [Fact]
    public void CommandTable_PreservesHistoricalPrecedence()
    {
        ICliCommand[] commands =
        [
            new McpTokenCommand(), new McpServeCommand(), new DoctorCommand(),
            new ImportModelSecretCommand(), new ListModelSecretsCommand(), new DeleteModelSecretCommand(),
            new ProbeModelCommand(), new BenchmarkModelCommand(), new ReportRangeCommand(),
            new AskCommand(), new RebuildVectorsCommand(), new BackupCommand(),
            new RestoreCommand(), new PurgeDataCommand(), new DiagnosticsOnlyCommand(),
            new StartupCommand()
        ];

        Assert.Same(commands[0], commands.First(command => command.Matches(["--mcp-token", "--doctor"])));
        Assert.Same(commands[2], commands.First(command => command.Matches(["--doctor"])));
        Assert.Same(commands[^2], commands.First(command => command.Matches(["--benchmark-asr"])));
        Assert.Same(commands[^1], commands.First(command => command.Matches(["--capture-smoke", "10"])));
    }

    [Fact]
    public void MaintenanceCommands_RequireDatabaseInitialization()
    {
        Assert.True(new BackupCommand().RequiresDatabaseInitialization);
        Assert.True(new RestoreCommand().RequiresDatabaseInitialization);
        Assert.True(new PurgeDataCommand().RequiresDatabaseInitialization);
        Assert.True(new RebuildVectorsCommand().RequiresDatabaseInitialization);
        Assert.True(new ReportRangeCommand().RequiresDatabaseInitialization);
        Assert.True(new AskCommand().RequiresDatabaseInitialization);
        Assert.False(new DoctorCommand().RequiresDatabaseInitialization);
        Assert.False(new StartupCommand().RequiresDatabaseInitialization);
    }

    [Fact]
    public void ExitCodeContract_UsesDocumentedValues()
    {
        Assert.Equal(0, CliResult.Done().ExitCode);
        Assert.Equal(1, CliResult.Done(1).ExitCode);
        Assert.Equal(2, CliResult.Done(2).ExitCode);
        Assert.Equal(3, CliResult.Done(3).ExitCode);
        Assert.False(CliResult.Done().StartDesktop);
        Assert.True(CliResult.Desktop().StartDesktop);
    }
}
