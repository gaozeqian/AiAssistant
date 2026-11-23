using AiAssistant.Application.Ports;

namespace AiAssistant.Host.Commands;

/// <summary>Explicit dependencies handed to a CLI command; no service locator inside commands.</summary>
internal sealed record CliCommandContext(AppRuntime Runtime, string[] Args)
{
    public RuntimeServices Services => Runtime.Services;

    public IClock Clock => Services.Clock;
}
