namespace AiAssistant.Application.Ports;

/// <summary>
/// User-editable retention policy. Media (screenshots and other encrypted blobs) and command audio
/// expire independently; a storage cap of zero means "no size limit". Both retention windows are
/// applied at write time, so changing them never rewrites already-persisted expiry stamps.
/// </summary>
public sealed record RetentionSettings(
    int MediaRetentionDays,
    int CommandAudioRetentionDays,
    int MaxStorageGigabytes)
{
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 365;
    public const int MaxStorageGigabyteLimit = 1000;

    public static readonly RetentionSettings Default = new(
        MediaRetentionDays: 30,
        CommandAudioRetentionDays: 7,
        MaxStorageGigabytes: 0);

    public TimeSpan MediaRetention => TimeSpan.FromDays(MediaRetentionDays);

    public TimeSpan CommandAudioRetention => TimeSpan.FromDays(CommandAudioRetentionDays);

    public long? MaxStorageBytes => MaxStorageGigabytes <= 0
        ? null
        : MaxStorageGigabytes * 1024L * 1024L * 1024L;

    public static RetentionSettings Normalize(
        int mediaRetentionDays,
        int commandAudioRetentionDays,
        int maxStorageGigabytes) =>
        new(
            Math.Clamp(mediaRetentionDays, MinRetentionDays, MaxRetentionDays),
            Math.Clamp(commandAudioRetentionDays, MinRetentionDays, MaxRetentionDays),
            Math.Clamp(maxStorageGigabytes, 0, MaxStorageGigabyteLimit));
}

/// <summary>
/// Persists the user-editable retention policy. The UI saves through this port; the retention sweep
/// and media writer read the applied options so a change takes effect without restarting the app.
/// </summary>
public interface IRetentionSettingsStore
{
    RetentionSettings Load();

    Task SaveAsync(RetentionSettings settings, CancellationToken ct);
}

/// <summary>Pushes a saved policy into the live storage/voice options.</summary>
public interface IRetentionPolicyApplier
{
    void Apply(RetentionSettings settings);
}
