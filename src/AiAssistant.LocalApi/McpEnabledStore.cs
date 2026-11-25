using System.Text.Json;

namespace AiAssistant.LocalApi;

/// <summary>
/// Persists the opt-in local MCP enable flag so the diagnostics-panel toggle survives a restart.
/// Unlike the bearer token this flag is not secret, so it is written as plain JSON.
/// The environment variable <c>AIASSISTANT_MCP_ENABLED</c>, when present, still wins at startup
/// (used by headless/CLI modes); the persisted flag only applies when the variable is unset.
/// </summary>
public sealed class McpEnabledStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public McpEnabledStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "local-mcp.json");
    }

    public bool Load()
    {
        if (!File.Exists(_filePath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize<McpEnabledDocument>(stream, JsonOptions)?.Enabled == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task SaveAsync(bool enabled, CancellationToken ct)
    {
        var tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, new McpEnabledDocument(enabled), JsonOptions, ct);
            }

            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private sealed record McpEnabledDocument(bool Enabled);
}
