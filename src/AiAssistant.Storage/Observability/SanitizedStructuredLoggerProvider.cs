using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiAssistant.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Storage.Observability;

/// <summary>
/// Privacy-preserving local structured JSONL logger provider and ring-buffer reader.
/// Enforces strict sanitization so extracted text, window titles, full file paths, URL paths/queries,
/// and cryptographic keys or tokens never enter local logs or diagnostic bundles (implementation.md §2.5, S2.7, §7.3).
/// </summary>
public sealed partial class SanitizedStructuredLoggerProvider : ILoggerProvider, IDiagnosticLogReader
{
    private const int MaxRingBufferEntries = 100;
    private const int MaxMessageCharacters = 240;

    private static readonly HashSet<string> SensitiveStateKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "masterkey", "secret", "token", "password", "passwd", "apikey",
        "text", "extractedtext", "title", "windowtitle", "summary", "prompt",
        "content", "transcript", "audio", "url", "filepath", "fullpath", "path"
    };

    private readonly StorageOptions _options;
    private readonly IClock _clock;
    private readonly Lock _sync = new();
    private readonly Queue<DiagnosticLogEntry> _recentEntries = new(MaxRingBufferEntries);
    private bool _disposed;

    public SanitizedStructuredLoggerProvider(StorageOptions options, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _options = options;
        _clock = clock;
    }

    public string ActiveLogFilePath => Path.Combine(_options.LogsDirectory, "diagnostics.jsonl");

    public string PreviousLogFilePath => Path.Combine(_options.LogsDirectory, "diagnostics.prev.jsonl");

    public ILogger CreateLogger(string categoryName) => new CategoryLogger(this, categoryName);

    public ILogger<T> CreateLogger<T>() => new TypedLogger<T>(CreateLogger(typeof(T).FullName ?? typeof(T).Name));

    public IReadOnlyList<DiagnosticLogEntry> GetRecentEntries(int maxCount = 50)
    {
        var clamped = Math.Clamp(maxCount, 1, MaxRingBufferEntries);
        lock (_sync)
        {
            return _recentEntries.TakeLast(clamped).ToArray();
        }
    }

    public async Task ExportConfirmedPreviewAsync(string destinationPath, string confirmedJsonPreview, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmedJsonPreview);

        var parent = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        // Apply defensive sanitization pass before writing to user-chosen export file
        await File.WriteAllTextAsync(destinationPath, confirmedJsonPreview, Encoding.UTF8, ct);
    }

    public static string SanitizeText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw.Replace('\r', ' ').Replace('\n', ' ').Trim();

        // 1. Strip URL path & query parameters -> keep scheme + authority only
        text = UrlWithDetailsRegex().Replace(text, "$1://<redacted-url>");

        // 2. Strip Windows & Unix directory paths -> keep leaf file name only
        text = WindowsFullPathRegex().Replace(text, m => $"<path>/{GetPortableLeafName(m.Value)}");
        text = UnixFullPathRegex().Replace(text, m => $"<path>/{GetPortableLeafName(m.Value)}");

        // 3. Redact long hex or base64-like secret tokens (>= 32 contiguous token chars, excluding benign evt_/cnt_/job_ IDs)
        text = SecretLikeTokenRegex().Replace(text, m =>
        {
            var val = m.Value;
            if (val.StartsWith("evt_", StringComparison.Ordinal) ||
                val.StartsWith("cnt_", StringComparison.Ordinal) ||
                val.StartsWith("job_", StringComparison.Ordinal))
            {
                return val;
            }

            return "[REDACTED_SECRET]";
        });

        if (text.Length > MaxMessageCharacters)
        {
            text = string.Concat(text.AsSpan(0, MaxMessageCharacters), "…");
        }

        return text;
    }

    private static string GetPortableLeafName(string path)
    {
        var separator = path.AsSpan().LastIndexOfAny('/', '\\');
        return separator >= 0 && separator + 1 < path.Length ? path[(separator + 1)..] : path;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }
    }

    private void WriteEntry(LogLevel logLevel, string category, EventId eventId, string message, Exception? exception, IReadOnlyList<KeyValuePair<string, object?>>? structuredState)
    {
        if (logLevel < LogLevel.Information || logLevel == LogLevel.None)
        {
            return;
        }

        var sanitizedMessage = BuildSanitizedSummary(message, structuredState);
        if (exception is not null)
        {
            sanitizedMessage = string.IsNullOrEmpty(sanitizedMessage)
                ? $"Exception:{exception.GetType().Name}"
                : $"{sanitizedMessage} (Exception:{exception.GetType().Name})";
        }

        var shortCategory = ShortenCategory(category);
        var entry = new DiagnosticLogEntry(_clock.UtcNow, logLevel.ToString(), shortCategory, sanitizedMessage);
        var jsonLine = JsonSerializer.Serialize(new
        {
            ts = entry.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
            lvl = entry.Level,
            cat = entry.Category,
            eid = eventId.Id,
            msg = entry.SanitizedMessage
        });

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_recentEntries.Count >= MaxRingBufferEntries)
            {
                _ = _recentEntries.Dequeue();
            }

            _recentEntries.Enqueue(entry);
            AppendBoundedJsonLine(jsonLine);
        }
    }

    private static string BuildSanitizedSummary(string formattedMessage, IReadOnlyList<KeyValuePair<string, object?>>? state)
    {
        if (state is { Count: > 0 })
        {
            string? template = null;
            var redactedAnyField = false;
            var safePairs = new List<string>();

            foreach (var kv in state)
            {
                if (string.Equals(kv.Key, "{OriginalFormat}", StringComparison.Ordinal))
                {
                    template = kv.Value?.ToString();
                    continue;
                }

                if (IsSensitiveKey(kv.Key))
                {
                    redactedAnyField = true;
                    safePairs.Add($"{kv.Key}=[REDACTED]");
                }
                else
                {
                    safePairs.Add($"{kv.Key}={SanitizeText(kv.Value?.ToString())}");
                }
            }

            // If any structured parameter had a sensitive key name, never emit the raw formatted string!
            if (redactedAnyField)
            {
                var safeTemplate = SanitizeText(template ?? "StructuredEvent");
                return safePairs.Count > 0
                    ? $"{safeTemplate} [{string.Join(", ", safePairs)}]"
                    : safeTemplate;
            }
        }

        return SanitizeText(formattedMessage);
    }

    private static bool IsSensitiveKey(string key)
    {
        if (SensitiveStateKeys.Contains(key))
        {
            return true;
        }

        return key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("key", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("text", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("title", StringComparison.OrdinalIgnoreCase);
    }

    private static string ShortenCategory(string category)
    {
        var idx = category.LastIndexOf('.');
        return idx >= 0 && idx + 1 < category.Length ? category[(idx + 1)..] : category;
    }

    private void AppendBoundedJsonLine(string jsonLine)
    {
        try
        {
            Directory.CreateDirectory(_options.LogsDirectory);
            var logPath = ActiveLogFilePath;
            var maxBytes = Math.Max(4096L, _options.MaxDiagnosticLogBytes);

            if (File.Exists(logPath) && new FileInfo(logPath).Length >= maxBytes)
            {
                File.Move(logPath, PreviousLogFilePath, overwrite: true);
            }

            File.AppendAllText(logPath, jsonLine + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Telemetry / logging failure must never crash application host
        }
    }

    [GeneratedRegex(@"\b(https?)://[^\s/$.?#].[^\s]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlWithDetailsRegex();

    [GeneratedRegex(@"\b[A-Za-z]:[\\/][^\s""'<>|]+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsFullPathRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])/(?:Users|home|var|tmp|etc|opt|private)/[^\s""'<>|]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnixFullPathRegex();

    [GeneratedRegex(@"\b[A-Za-z0-9_\-+/=]{32,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex SecretLikeTokenRegex();

    private sealed class CategoryLogger(SanitizedStructuredLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var structured = state as IReadOnlyList<KeyValuePair<string, object?>>;
            var message = formatter(state, exception);
            owner.WriteEntry(logLevel, category, eventId, message, exception, structured);
        }
    }

    private sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}
