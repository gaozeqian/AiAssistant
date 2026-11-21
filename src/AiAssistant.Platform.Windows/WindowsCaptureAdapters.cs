using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Platform.Windows;

internal static partial class Win32Native
{
    public const int SmCxScreen = 0;
    public const int SmCyScreen = 1;
    public const int Srccopy = 0x00CC0020;
    public const int GwlStyle = -16;
    public const int EsPassword = 0x0020;
    public const uint WmGetTextLength = 0x000E;
    public const uint WmGetText = 0x000D;
    public const uint SmtoAbortIfHung = 0x0002;
    public static readonly nuint HkeyCurrentUser = 0x80000001u;
    public const int KeyRead = 0x20019;
    public const int KeyWrite = 0x20006;
    public const uint RegSz = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint BiSize;
        public int BiWidth;
        public int BiHeight;
        public ushort BiPlanes;
        public ushort BiBitCount;
        public uint BiCompression;
        public uint BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public uint BiClrUsed;
        public uint BiClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfo
    {
        public BitmapInfoHeader BmiHeader;
    }

    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(nint hWnd, [Out] char[] lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLengthW(nint hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint hWnd, nint hDc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    public static extern int GetWindowLongW(nint hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint SendMessageTimeoutW(
        nint hWnd,
        uint msg,
        nuint wParam,
        [Out] char[]? lParam,
        uint fuFlags,
        uint uTimeout,
        out nuint lpdwResult);

    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    public static extern nint CreateDIBSection(nint hdc, ref BitmapInfo pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [DllImport("gdi32.dll")]
    public static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(nint ho);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool StretchBlt(
        nint hdcDest, int xDest, int yDest, int wDest, int hDest,
        nint hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);

    [DllImport("gdi32.dll")]
    public static extern int SetStretchBltMode(nint hdc, int mode);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegOpenKeyExW(nuint hKey, string lpSubKey, uint ulOptions, int samDesired, out nuint phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegQueryValueExW(nuint hKey, string lpValueName, nint lpReserved, out uint lpType, nint lpData, ref uint lpcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegSetValueExW(nuint hKey, string lpValueName, uint reserved, uint dwType, byte[] lpData, uint cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegDeleteValueW(nuint hKey, string lpValueName);

    [DllImport("advapi32.dll")]
    public static extern int RegCloseKey(nuint hKey);
}

/// <summary>
/// Event-driven foreground window &amp; focus source with 10s periodic fallback heartbeat (DESIGN.md §3.2, §9).
/// Never captures keystrokes or clipboard contents.
/// </summary>
public sealed class WindowsWindowActivitySource(IClock clock, IScreenCapture? screenCapture = null) : IWindowActivitySource
{
    public SourceKind Kind => SourceKind.WindowActivity;

    public CapabilityState Capability => OperatingSystem.IsWindows()
        ? CapabilityState.Supported(CapabilityKind.WindowActivity, "Win32 ForegroundWindow + Process metadata")
        : CapabilityState.Unavailable(CapabilityKind.WindowActivity, "Windows adapter inactive on non-Windows host");

    public Task<CapturedSample?> GetCurrentForegroundAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<CapturedSample?>(null);
        }

        var hwnd = Win32Native.GetForegroundWindow();
        if (hwnd == 0)
        {
            return Task.FromResult<CapturedSample?>(null);
        }

        var titleLen = Win32Native.GetWindowTextLengthW(hwnd);
        var titleBuffer = new char[Math.Max(titleLen + 2, 256)];
        var copied = Win32Native.GetWindowTextW(hwnd, titleBuffer, titleBuffer.Length);
        var title = copied > 0 ? new string(titleBuffer, 0, copied).Trim() : string.Empty;

        _ = Win32Native.GetWindowThreadProcessId(hwnd, out var pid);
        var appName = TryResolveProcessName((int)pid);
        if (string.IsNullOrWhiteSpace(title) && string.Equals(appName, "Unknown", StringComparison.Ordinal))
        {
            return Task.FromResult<CapturedSample?>(null);
        }

        var sample = new CapturedSample(
            clock.Now(),
            SourceKind.WindowActivity,
            appName,
            string.IsNullOrWhiteSpace(title) ? appName : title,
            NativeWindowHandle: hwnd);

        return Task.FromResult<CapturedSample?>(sample);
    }

    public async IAsyncEnumerable<CapturedSample> CaptureAsync(
        CaptureContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        var fallbackInterval = context.FallbackInterval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(10)
            : context.FallbackInterval;

        string? lastSignature = null;
        var lastEmitUtc = DateTimeOffset.MinValue;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(400));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var current = await GetCurrentForegroundAsync(ct);
            if (current is null)
            {
                continue;
            }

            var signature = $"{current.AppName}\u001F{current.WindowTitle}";
            var nowUtc = clock.UtcNow;
            var isChanged = !string.Equals(signature, lastSignature, StringComparison.Ordinal);
            var isFallbackDue = (nowUtc - lastEmitUtc) >= fallbackInterval;

            if (!isChanged && !isFallbackDue)
            {
                continue;
            }

            lastSignature = signature;
            lastEmitUtc = nowUtc;

            if (screenCapture is not null && screenCapture.Capability.IsOperational)
            {
                var frame = await screenCapture.CaptureFrameAsync(ct);
                if (frame is not null)
                {
                    yield return current with
                    {
                        Source = SourceKind.ScreenFrame,
                        RawMediaBytes = frame.EncodedPngBytes,
                        MediaKind = ContentKind.ScreenshotPng,
                        PerceptualHash = frame.PerceptualHash
                    };
                    continue;
                }
            }

            yield return current;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string TryResolveProcessName(int pid)
    {
        if (pid <= 0)
        {
            return "Unknown";
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            return string.IsNullOrWhiteSpace(proc.ProcessName) ? "Unknown" : proc.ProcessName;
        }
        catch
        {
            return "Unknown";
        }
    }
}

/// <summary>
/// Captures desktop screen via Win32 GDI StretchBlt (downscaled to <= 1280px width for <1% CPU overhead),
/// computes 64-bit perceptual hash, and encodes PNG in memory.
/// </summary>
public sealed class WindowsScreenCapture(IClock clock) : IScreenCapture
{
    public CapabilityState Capability => OperatingSystem.IsWindows()
        ? CapabilityState.Supported(CapabilityKind.ScreenCapture, "Win32 GDI BitBlt/StretchBlt + PNG + pHash")
        : CapabilityState.Unavailable(CapabilityKind.ScreenCapture, "Windows screen capture inactive on non-Windows host");

    public Task<ScreenCaptureFrame?> CaptureFrameAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<ScreenCaptureFrame?>(null);
        }

        var srcWidth = Win32Native.GetSystemMetrics(Win32Native.SmCxScreen);
        var srcHeight = Win32Native.GetSystemMetrics(Win32Native.SmCyScreen);
        if (srcWidth <= 0 || srcHeight <= 0)
        {
            return Task.FromResult<ScreenCaptureFrame?>(null);
        }

        var targetWidth = Math.Min(srcWidth, 1280);
        var targetHeight = Math.Max(1, (int)Math.Round((double)srcHeight * targetWidth / srcWidth));

        var screenDc = Win32Native.GetDC(0);
        if (screenDc == 0)
        {
            return Task.FromResult<ScreenCaptureFrame?>(null);
        }

        var memDc = Win32Native.CreateCompatibleDC(screenDc);
        nint dib = 0;
        nint oldObj = 0;

        try
        {
            var bmi = new Win32Native.BitmapInfo
            {
                BmiHeader = new Win32Native.BitmapInfoHeader
                {
                    BiSize = (uint)Marshal.SizeOf<Win32Native.BitmapInfoHeader>(),
                    BiWidth = targetWidth,
                    BiHeight = -targetHeight, // Top-down DIB
                    BiPlanes = 1,
                    BiBitCount = 32,
                    BiCompression = 0
                }
            };

            dib = Win32Native.CreateDIBSection(memDc, ref bmi, 0, out var bitsPtr, 0, 0);
            if (dib == 0 || bitsPtr == 0)
            {
                return Task.FromResult<ScreenCaptureFrame?>(null);
            }

            oldObj = Win32Native.SelectObject(memDc, dib);
            _ = Win32Native.SetStretchBltMode(memDc, 3); // COLORONCOLOR fast downscale
            var ok = Win32Native.StretchBlt(
                memDc, 0, 0, targetWidth, targetHeight,
                screenDc, 0, 0, srcWidth, srcHeight,
                Win32Native.Srccopy);

            if (!ok)
            {
                return Task.FromResult<ScreenCaptureFrame?>(null);
            }

            var stride = targetWidth * 4;
            var rawPixels = new byte[stride * targetHeight];
            Marshal.Copy(bitsPtr, rawPixels, 0, rawPixels.Length);

            var phash = PerceptualHash.ComputeFromBgra32(rawPixels, targetWidth, targetHeight, stride);
            var pngBytes = PngEncoder.EncodeBgra32(rawPixels, targetWidth, targetHeight, stride);

            return Task.FromResult<ScreenCaptureFrame?>(
                new ScreenCaptureFrame(clock.Now(), targetWidth, targetHeight, phash, pngBytes));
        }
        finally
        {
            if (oldObj != 0 && memDc != 0)
            {
                _ = Win32Native.SelectObject(memDc, oldObj);
            }

            if (dib != 0)
            {
                _ = Win32Native.DeleteObject(dib);
            }

            if (memDc != 0)
            {
                _ = Win32Native.DeleteDC(memDc);
            }

            _ = Win32Native.ReleaseDC(0, screenDc);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Monitors ONLY user-whitelisted directories and reads text snippets strictly within size &amp; extension limits (DESIGN.md §3.2).
/// </summary>
public sealed class WhitelistedFileActivitySource(IClock clock) : IFileActivitySource
{
    private static readonly HashSet<string> AllowedTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".cs", ".json", ".yaml", ".yml", ".xml", ".axaml", ".ts", ".tsx", ".js", ".py", ".sql", ".sln", ".csproj"
    };

    public SourceKind Kind => SourceKind.FileActivity;

    public CapabilityState Capability =>
        CapabilityState.Supported(CapabilityKind.FileActivity, "Whitelisted directory FileSystemWatcher (<=200KB text/code only)");

    public async IAsyncEnumerable<CapturedSample> CaptureAsync(
        CaptureContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var validDirs = context.WhitelistedDirectories
            .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (validDirs.Length == 0)
        {
            yield break;
        }

        var channel = Channel.CreateBounded<CapturedSample>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var watchers = new List<FileSystemWatcher>(validDirs.Length);
        try
        {
            foreach (var dir in validDirs)
            {
                var watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                };

                void HandleChange(string fullPath, string changeKind)
                {
                    var fileName = Path.GetFileName(fullPath);
                    if (string.IsNullOrWhiteSpace(fileName) || fileName.StartsWith('.'))
                    {
                        return;
                    }

                    var snippet = TryReadSafeTextSnippet(fullPath, context.MaxTextFileBytes);
                    var meta = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["operation"] = changeKind,
                        ["fileName"] = fileName,
                        ["extension"] = Path.GetExtension(fullPath)
                    };

                    var sample = new CapturedSample(
                        clock.Now(),
                        SourceKind.FileActivity,
                        "FileSystem",
                        $"{changeKind}: {fileName}",
                        ExtractedText: snippet,
                        Metadata: meta);

                    channel.Writer.TryWrite(sample);
                }

                watcher.Created += (_, e) => HandleChange(e.FullPath, "Created");
                watcher.Changed += (_, e) => HandleChange(e.FullPath, "Modified");
                watcher.Renamed += (_, e) => HandleChange(e.FullPath, "Renamed");
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }

            await foreach (var item in channel.Reader.ReadAllAsync(ct))
            {
                yield return item;
            }
        }
        finally
        {
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }

            channel.Writer.TryComplete();
        }
    }

    public static string? TryReadSafeTextSnippet(string fullPath, int maxBytes)
    {
        var ext = Path.GetExtension(fullPath);
        if (!AllowedTextExtensions.Contains(ext))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length <= 0 || info.Length > maxBytes)
            {
                return null;
            }

            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[Math.Min(2048, maxBytes)];
            var read = reader.Read(buffer, 0, buffer.Length);
            return read > 0 ? new string(buffer, 0, read) : null;
        }
        catch
        {
            return null;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Accessibility-first text extractor: inspects Win32 accessible child controls (strictly skipping ES_PASSWORD controls)
/// and falls back to pluggable OCR delegate when accessibility yields no text (IMPLEMENTATION.md S2.3).
/// </summary>
public sealed class WindowsTextExtractor(Func<byte[], CancellationToken, Task<string?>>? ocrFallback = null) : ITextExtractor
{
    public CapabilityState AccessibilityCapability => OperatingSystem.IsWindows()
        ? CapabilityState.Supported(CapabilityKind.AccessibilityText, "Win32 UI control tree (ES_PASSWORD excluded)")
        : CapabilityState.Unavailable(CapabilityKind.AccessibilityText, "Windows accessibility inactive on non-Windows host");

    public CapabilityState OcrCapability => ocrFallback is not null
        ? CapabilityState.Supported(CapabilityKind.Ocr, "Configured OCR fallback")
        : OperatingSystem.IsWindows()
            ? CapabilityState.Degraded(CapabilityKind.Ocr, "Windows.Media.Ocr on-demand fallback (accessibility tree active)")
            : CapabilityState.Unavailable(CapabilityKind.Ocr, "Windows OCR inactive on non-Windows host");

    public async Task<ExtractedTextResult> ExtractAsync(TextExtractionRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (OperatingSystem.IsWindows() && request.WindowHandle != 0)
        {
            var accessibleText = ExtractAccessibleChildTexts(request.WindowHandle);
            if (!string.IsNullOrWhiteSpace(accessibleText))
            {
                return new ExtractedTextResult(accessibleText, TextExtractionMethod.AccessibilityTree, 0.95);
            }
        }

        if (ocrFallback is not null && request.ScreenshotPngBytes is { Length: > 0 } png)
        {
            var ocrText = await ocrFallback(png, ct);
            if (!string.IsNullOrWhiteSpace(ocrText))
            {
                return new ExtractedTextResult(ocrText.Trim(), TextExtractionMethod.SystemOcr, 0.80);
            }
        }

        var fallbackTitle = string.IsNullOrWhiteSpace(request.WindowTitle)
            ? request.AppName
            : $"{request.AppName} - {request.WindowTitle}";

        return new ExtractedTextResult(fallbackTitle, TextExtractionMethod.None, 0.50);
    }

    private static string ExtractAccessibleChildTexts(nint rootHwnd)
    {
        var parts = new List<string>();
        var totalChars = 0;

        _ = Win32Native.EnumChildWindows(rootHwnd, (childHwnd, _) =>
        {
            if (totalChars >= 2048)
            {
                return false;
            }

            var style = Win32Native.GetWindowLongW(childHwnd, Win32Native.GwlStyle);
            if ((style & Win32Native.EsPassword) != 0)
            {
                // Privacy boundary: never read password controls!
                return true;
            }

            if (Win32Native.SendMessageTimeoutW(
                    childHwnd,
                    Win32Native.WmGetTextLength,
                    0,
                    null,
                    Win32Native.SmtoAbortIfHung,
                    40,
                    out var lenResult) == 0)
            {
                return true;
            }

            var length = (int)Math.Min((ulong)lenResult, 512UL);
            if (length <= 1)
            {
                return true;
            }

            var buf = new char[length + 2];
            if (Win32Native.SendMessageTimeoutW(
                    childHwnd,
                    Win32Native.WmGetText,
                    (nuint)buf.Length,
                    buf,
                    Win32Native.SmtoAbortIfHung,
                    40,
                    out var copiedChars) != 0)
            {
                var actualLen = (int)Math.Min((ulong)copiedChars, (ulong)buf.Length);
                var text = new string(buf, 0, actualLen).TrimEnd('\0').Trim();
                if (text.Length > 1 && !parts.Contains(text, StringComparer.Ordinal))
                {
                    parts.Add(text);
                    totalChars += text.Length;
                }
            }

            return true;
        }, 0);

        return string.Join(" | ", parts);
    }
}

/// <summary>
/// Optional Windows user-level startup switch via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// Disabled by default; never requires administrator elevation.
/// </summary>
public sealed class WindowsAutoStartService(string executableCommand, string valueName = "AiAssistant") : IAutoStartService
{
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public CapabilityState Capability => OperatingSystem.IsWindows()
        ? CapabilityState.Supported(CapabilityKind.AutoStart, @"HKCU\...\Run user auto-start")
        : CapabilityState.Unavailable(CapabilityKind.AutoStart, "Windows registry auto-start inactive on non-Windows host");

    public Task<bool> IsEnabledAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(false);
        }

        if (Win32Native.RegOpenKeyExW(Win32Native.HkeyCurrentUser, RunSubKey, 0, Win32Native.KeyRead, out var hKey) != 0)
        {
            return Task.FromResult(false);
        }

        try
        {
            uint size = 0;
            var rc = Win32Native.RegQueryValueExW(hKey, valueName, 0, out _, 0, ref size);
            return Task.FromResult(rc == 0 && size > 0);
        }
        finally
        {
            _ = Win32Native.RegCloseKey(hKey);
        }
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        if (Win32Native.RegOpenKeyExW(Win32Native.HkeyCurrentUser, RunSubKey, 0, Win32Native.KeyWrite, out var hKey) != 0)
        {
            return Task.CompletedTask;
        }

        try
        {
            if (enabled)
            {
                var bytes = Encoding.Unicode.GetBytes(executableCommand + '\0');
                _ = Win32Native.RegSetValueExW(hKey, valueName, 0, Win32Native.RegSz, bytes, (uint)bytes.Length);
            }
            else
            {
                _ = Win32Native.RegDeleteValueW(hKey, valueName);
            }
        }
        finally
        {
            _ = Win32Native.RegCloseKey(hKey);
        }

        return Task.CompletedTask;
    }
}

public sealed class WindowsCapabilityProvider(
    IScreenCapture screenCapture,
    IWindowActivitySource windowActivitySource,
    IFileActivitySource fileActivitySource,
    ITextExtractor textExtractor,
    IKeyStore keyStore,
    IAutoStartService autoStartService) : ICapabilityProvider
{
    public string PlatformName => "Windows (win-x64)";

    public IReadOnlyList<CapabilityState> GetCapabilities() =>
    [
        screenCapture.Capability,
        windowActivitySource.Capability,
        fileActivitySource.Capability,
        textExtractor.AccessibilityCapability,
        textExtractor.OcrCapability,
        keyStore.Capability,
        autoStartService.Capability,
        CapabilityState.Supported(CapabilityKind.SystemTray, "Avalonia TrayIcon active"),
        CapabilityState.Supported(CapabilityKind.AudioCapture, "WASAPI shared-mode capture through AudioHub"),
        CapabilityState.Degraded(
            CapabilityKind.WakeWord,
            "Voice is opt-in; enable it on the Voice page",
            "Requires sherpa-onnx KWS/TTS models under AIASSISTANT_SHERPA_MODEL_DIR.")
    ];
}
