using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.Ports;

/// <summary>
/// Frame-size policy shared by every platform adapter so Windows, macOS and Linux downsample to the
/// same budget. 1280 (the original cap) discarded half of a 2560px display before the timeline ever
/// saw it, which is what made media previews unreadable; 1920 keeps 12px UI text recognizable.
/// </summary>
public static class ScreenCaptureLimits
{
    public const int MaxFrameWidth = 1920;
}

/// <summary>Identity and virtual-screen bounds of one display.</summary>
public sealed record DisplayInfo(
    string Id,
    string Name,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary);

/// <summary>
/// One captured display frame. A multi-monitor desktop yields one frame per display so each
/// monitor gets its own activity record instead of only the primary screen being stored.
/// </summary>
public sealed record ScreenCaptureFrame(
    OccurredTimestamp Timestamp,
    int Width,
    int Height,
    ulong PerceptualHash,
    byte[] EncodedPngBytes,
    DisplayInfo? Display = null,
    bool IsForegroundDisplay = false);

/// <summary>What to capture: the foreground window handle lets the adapter pick its display.</summary>
public sealed record ScreenCaptureTarget(nint WindowHandle = 0);

/// <summary>Stable event metadata keys describing which display a frame came from.</summary>
public static class ScreenCaptureMetadata
{
    public const string DisplayId = "display.id";
    public const string DisplayName = "display.name";
    public const string DisplayBounds = "display.bounds";
    public const string DisplayPrimary = "display.primary";
    public const string DisplayForeground = "display.foreground";

    public static IReadOnlyDictionary<string, string> FromDisplay(ScreenCaptureFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Display is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DisplayId] = frame.Display.Id,
            [DisplayName] = frame.Display.Name,
            [DisplayBounds] = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{frame.Display.Left},{frame.Display.Top},{frame.Display.Width},{frame.Display.Height}"),
            [DisplayPrimary] = frame.Display.IsPrimary ? "true" : "false",
            [DisplayForeground] = frame.IsForegroundDisplay ? "true" : "false"
        };
    }
}
