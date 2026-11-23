namespace AiAssistant.Host.Commands;

/// <summary>Outcome of one CLI command: the process exit code and whether the desktop shell must start.</summary>
internal readonly record struct CliResult(int ExitCode, bool StartDesktop)
{
    public static CliResult Done(int exitCode = 0) => new(exitCode, StartDesktop: false);

    public static CliResult Desktop() => new(0, StartDesktop: true);
}
