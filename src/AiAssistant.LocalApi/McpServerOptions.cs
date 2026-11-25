namespace AiAssistant.LocalApi;

/// <summary>Assembly marker for architecture boundary verification.</summary>
public static class LocalApiAssemblyMarker;

/// <summary>Opt-in configuration. The local MCP endpoint is always disabled unless explicitly enabled.</summary>
public sealed class McpServerOptions
{
    public bool Enabled { get; set; }
    public int Port { get; init; } = 47831;
    public int MaxRequestsPerMinute { get; init; } = 60;
    public int MaxBodyBytes { get; init; } = 64 * 1024;
}
