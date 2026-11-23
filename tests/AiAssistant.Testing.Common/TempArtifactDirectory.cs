using AiAssistant.Core.Configuration;

namespace AiAssistant.Testing.Common;

/// <summary>
/// Test scratch directory under <c>artifacts/tmp/&lt;suite&gt;/&lt;guid&gt;</c>. Never writes into
/// <c>AppContext.BaseDirectory</c> or a hardcoded system temp path, and cleans up on dispose.
/// </summary>
public sealed class TempArtifactDirectory : IDisposable, IAsyncDisposable
{
    public TempArtifactDirectory(string suite, string? relativeSubdirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suite);
        var root = System.IO.Path.Combine(
            ArtifactPaths.ResolveRoot(),
            "tmp",
            suite,
            Guid.NewGuid().ToString("N"));
        Path = relativeSubdirectory is null ? root : System.IO.Path.Combine(root, relativeSubdirectory);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>
    /// Creates a unique scratch path for tests that already own cleanup in their Dispose method.
    /// New tests should hold a <see cref="TempArtifactDirectory"/> instance instead.
    /// </summary>
    public static string CreatePath(string suite)
    {
        var path = System.IO.Path.Combine(
            ArtifactPaths.ResolveRoot(),
            "tmp",
            suite,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public string Combine(params string[] segments) => System.IO.Path.Combine([Path, .. segments]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup on Windows file-handle races.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; the next test run reuses a fresh GUID.
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
