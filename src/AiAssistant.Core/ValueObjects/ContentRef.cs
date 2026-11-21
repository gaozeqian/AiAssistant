namespace AiAssistant.Core.ValueObjects;

public enum ContentKind
{
    ScreenshotPng = 0,
    ScreenshotWebp = 1,
    ThumbnailPng = 2,
    AudioOpus = 3,
    TextSnapshot = 4,
    AudioPcm16Wav = 5
}

/// <summary>
/// Reference to an AES-GCM encrypted media blob managed with reference counting and retention policy.
/// </summary>
public sealed record ContentRef(
    string ContentId,
    ContentKind Kind,
    string Sha256Hex,
    long PlaintextLength,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    ulong? PerceptualHash = null);
