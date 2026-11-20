using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Core.Domain;

public enum SourceKind
{
    WindowActivity = 0,
    ScreenFrame = 1,
    FileActivity = 2,
    MicrophoneAudio = 3,
    BrowserTab = 4,
    SystemControl = 5
}

public enum PrivacyLevel
{
    LocalOnly = 0,
    InternalNetworkAllowed = 1,
    PublicCloudRedacted = 2,
    Excluded = 3
}

/// <summary>
/// Canonical captured activity event persisted in the encrypted event store.
/// </summary>
public sealed record ActivityEvent
{
    public string Id { get; init; }
    public OccurredTimestamp Timestamp { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public SourceKind Source { get; init; }
    public string AppName { get; init; }
    public string WindowTitle { get; init; }
    public string? ExtractedText { get; init; }
    public string? Summary { get; init; }
    public ContentRef? MediaRef { get; init; }
    public ulong? PerceptualHash { get; init; }
    public PrivacyLevel Privacy { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; }

    public ActivityEvent(
        string id,
        OccurredTimestamp timestamp,
        SourceKind source,
        string appName,
        string windowTitle,
        string? extractedText = null,
        string? summary = null,
        ContentRef? mediaRef = null,
        ulong? perceptualHash = null,
        PrivacyLevel privacy = PrivacyLevel.InternalNetworkAllowed,
        DateTimeOffset? endedAtUtc = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentNullException.ThrowIfNull(appName);
        ArgumentNullException.ThrowIfNull(windowTitle);

        Id = id;
        Timestamp = timestamp;
        Source = source;
        AppName = appName;
        WindowTitle = windowTitle;
        ExtractedText = extractedText;
        Summary = summary;
        MediaRef = mediaRef;
        PerceptualHash = perceptualHash;
        Privacy = privacy;
        EndedAtUtc = endedAtUtc?.ToUniversalTime();
        Metadata = metadata ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
