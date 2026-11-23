namespace AiAssistant.Testing.Common;

/// <summary>
/// Mutates process environment variables and restores the previous values on dispose. Tests that
/// use it must join the serial <see cref="ProcessEnvironmentCollection"/>.
/// </summary>
public sealed class ProcessEnvironmentScope : IDisposable
{
    private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);
    private bool _disposed;

    public ProcessEnvironmentScope(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (name, value) in values)
        {
            _previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var (name, value) in _previous)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}

/// <summary>Collection name for tests mutating process-wide environment variables.</summary>
public static class ProcessEnvironmentCollectionName
{
    public const string Value = "ProcessEnvironment";
}
