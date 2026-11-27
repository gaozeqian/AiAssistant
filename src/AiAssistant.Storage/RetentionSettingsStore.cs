using System.Globalization;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Configuration;

namespace AiAssistant.Storage;

/// <summary>
/// Persists the retention policy as a small JSON document in the user data directory.
/// The UI is the primary editor; environment variables only seed the first launch so a
/// headless/CLI install can still override the defaults.
/// </summary>
public sealed class RetentionSettingsStore : IRetentionSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly Lock _sync = new();
    private RetentionSettings _settings;

    public RetentionSettingsStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "retention.json");
        _settings = LoadInitial();
    }

    public RetentionSettings Load()
    {
        lock (_sync)
        {
            return _settings;
        }
    }

    public async Task SaveAsync(RetentionSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var document = new RetentionDocument(
            settings.MediaRetentionDays,
            settings.CommandAudioRetentionDays,
            settings.MaxStorageGigabytes);
        var tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, ct);
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

        lock (_sync)
        {
            _settings = settings;
        }
    }

    private RetentionSettings LoadInitial() =>
        TryLoadPersisted(out var persisted) ? persisted : LoadEnvironmentFallback();

    private bool TryLoadPersisted(out RetentionSettings settings)
    {
        settings = RetentionSettings.Default;
        if (!File.Exists(_filePath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = JsonSerializer.Deserialize<RetentionDocument>(stream, JsonOptions);
            if (document is null)
            {
                return false;
            }

            settings = RetentionSettings.Normalize(
                document.MediaRetentionDays,
                document.CommandAudioRetentionDays,
                document.MaxStorageGigabytes);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static RetentionSettings LoadEnvironmentFallback() =>
        RetentionSettings.Normalize(
            ReadBoundedInt(EnvironmentNames.MediaRetentionDays, RetentionSettings.Default.MediaRetentionDays),
            ReadBoundedInt(EnvironmentNames.VoiceAudioRetentionDays, RetentionSettings.Default.CommandAudioRetentionDays),
            ReadBoundedInt(EnvironmentNames.MaxStorageGigabytes, RetentionSettings.Default.MaxStorageGigabytes));

    private static int ReadBoundedInt(string name, int fallback) =>
        int.TryParse(
            Environment.GetEnvironmentVariable(name),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : fallback;

    private sealed record RetentionDocument(
        int MediaRetentionDays,
        int CommandAudioRetentionDays,
        int MaxStorageGigabytes);
}
