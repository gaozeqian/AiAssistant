using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Platform.macOS.Ipc;

namespace AiAssistant.Platform.macOS;

/// <summary>
/// macOS ScreenCaptureKit adapter over versioned Swift Helper IPC (ADR-0005, S3).
/// </summary>
public sealed class ScreenCapture(IMacHelperSupervisor supervisor, IClock clock) : IScreenCapture
{
    public CapabilityState Capability => MacHelperCapabilityEvaluator.EvaluateHelperCapability(
        supervisor,
        CapabilityKind.ScreenCapture,
        supervisor.Permissions.ScreenRecording,
        "ScreenCaptureKit（Swift Helper，≤1280px PNG + 感知哈希）",
        "请在「系统设置 > 隐私与安全性 > 屏幕与系统音频录制」中启用 AiAssistant。");

    public async Task<ScreenCaptureFrame?> CaptureFrameAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var response = await supervisor.InvokeAsync(
            new MacIpcRequest(string.Empty, MacHelperProtocol.Methods.ScreenCapture, MaxWidth: 1280),
            ct);

        if (!response.Ok || response.ScreenFrame is null || string.IsNullOrWhiteSpace(response.ScreenFrame.PngBase64))
        {
            return null;
        }

        byte[] pngBytes;
        try
        {
            pngBytes = Convert.FromBase64String(response.ScreenFrame.PngBase64);
        }
        catch (FormatException)
        {
            return null;
        }

        if (pngBytes.Length == 0)
        {
            return null;
        }

        return new ScreenCaptureFrame(
            clock.Now(),
            response.ScreenFrame.Width,
            response.ScreenFrame.Height,
            response.ScreenFrame.PerceptualHash,
            pngBytes);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// macOS NSWorkspace + AXUIElement foreground window adapter over Swift Helper IPC.
/// Resilient across helper restarts: transient IPC disconnects skip ticks without terminating the capture stream.
/// </summary>
public sealed class WindowActivitySource(
    IMacHelperSupervisor supervisor,
    IClock clock,
    IScreenCapture? screenCapture = null,
    TimeSpan? pollInterval = null) : IWindowActivitySource
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(400);

    public SourceKind Kind => SourceKind.WindowActivity;

    public CapabilityState Capability => MacHelperCapabilityEvaluator.EvaluateHelperCapability(
        supervisor,
        CapabilityKind.WindowActivity,
        supervisor.Permissions.Accessibility,
        "NSWorkspace + AXUIElement 前台窗口识别",
        "请在「系统设置 > 隐私与安全性 > 辅助功能」中启用 AiAssistant 以获取完整窗口标题。",
        degradeInsteadOfRequirePermission: true);

    public async Task<CapturedSample?> GetCurrentForegroundAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var response = await supervisor.InvokeAsync(
            new MacIpcRequest(string.Empty, MacHelperProtocol.Methods.ForegroundWindow),
            ct);

        if (!response.Ok || response.Window is null || string.IsNullOrWhiteSpace(response.Window.AppName))
        {
            return null;
        }

        var meta = string.IsNullOrWhiteSpace(response.Window.BundleId)
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal) { ["bundleId"] = response.Window.BundleId! };

        var title = string.IsNullOrWhiteSpace(response.Window.WindowTitle)
            ? response.Window.AppName
            : response.Window.WindowTitle;

        return new CapturedSample(
            clock.Now(),
            SourceKind.WindowActivity,
            response.Window.AppName,
            title,
            NativeWindowHandle: (nint)response.Window.WindowId,
            Metadata: meta);
    }

    public async IAsyncEnumerable<CapturedSample> CaptureAsync(
        CaptureContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var fallbackInterval = context.FallbackInterval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(10)
            : context.FallbackInterval;

        string? lastSignature = null;
        var lastEmitUtc = DateTimeOffset.MinValue;

        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (supervisor.Status is MacHelperSupervisorStatus.Unavailable or MacHelperSupervisorStatus.ProtocolIncompatible)
            {
                yield break;
            }

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
}

/// <summary>
/// macOS FSEvents whitelisted directory activity source via Swift Helper IPC with managed FileSystemWatcher fallback.
/// Enforces strict <=200KB text/code extension limits (design.md §3.1, §3.2).
/// </summary>
public sealed class FileActivitySource(
    IMacHelperSupervisor supervisor,
    IClock clock,
    TimeSpan? pollInterval = null) : IFileActivitySource
{
    private static readonly HashSet<string> AllowedTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".cs", ".swift", ".json", ".yaml", ".yml", ".xml", ".axaml", ".ts", ".tsx", ".js", ".py", ".sql", ".sln", ".csproj"
    };

    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);

    public SourceKind Kind => SourceKind.FileActivity;

    public CapabilityState Capability => supervisor.Status == MacHelperSupervisorStatus.Healthy
        ? CapabilityState.Supported(CapabilityKind.FileActivity, "FSEvents（Swift Helper，≤200KB 白名单文本/代码）")
        : CapabilityState.Degraded(CapabilityKind.FileActivity, "托管 FileSystemWatcher 兜底（≤200KB 白名单文本/代码）");

    public async IAsyncEnumerable<CapturedSample> CaptureAsync(
        CaptureContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var validDirs = context.WhitelistedDirectories
            .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (validDirs.Length == 0)
        {
            yield break;
        }

        // If Swift helper is active, poll FSEvents batch from helper; otherwise fall back to local FileSystemWatcher
        if (supervisor.Status == MacHelperSupervisorStatus.Healthy)
        {
            using var timer = new PeriodicTimer(_pollInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                var resp = await supervisor.InvokeAsync(
                    new MacIpcRequest(string.Empty, MacHelperProtocol.Methods.PollFileEvents, Directories: validDirs),
                    ct);

                if (!resp.Ok || resp.FileChanges is not { Count: > 0 })
                {
                    continue;
                }

                foreach (var change in resp.FileChanges)
                {
                    var sample = BuildSafeFileSample(change.FullPath, change.Operation, context.MaxTextFileBytes);
                    if (sample is not null)
                    {
                        yield return sample;
                    }
                }
            }

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

                void PushChange(string fullPath, string op)
                {
                    var sample = BuildSafeFileSample(fullPath, op, context.MaxTextFileBytes);
                    if (sample is not null)
                    {
                        channel.Writer.TryWrite(sample);
                    }
                }

                watcher.Created += (_, e) => PushChange(e.FullPath, "Created");
                watcher.Changed += (_, e) => PushChange(e.FullPath, "Modified");
                watcher.Renamed += (_, e) => PushChange(e.FullPath, "Renamed");
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }

            await foreach (var sample in channel.Reader.ReadAllAsync(ct))
            {
                yield return sample;
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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private CapturedSample? BuildSafeFileSample(string fullPath, string operation, int maxBytes)
    {
        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(fileName) || fileName.StartsWith('.'))
        {
            return null;
        }

        var snippet = TryReadSafeTextSnippet(fullPath, maxBytes);
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["operation"] = operation,
            ["fileName"] = fileName,
            ["extension"] = Path.GetExtension(fullPath)
        };

        return new CapturedSample(
            clock.Now(),
            SourceKind.FileActivity,
            "Finder/FSEvents",
            $"{operation}: {fileName}",
            ExtractedText: snippet,
            Metadata: meta);
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
            var buf = new char[Math.Min(2048, maxBytes)];
            var count = reader.Read(buf, 0, buf.Length);
            return count > 0 ? new string(buf, 0, count) : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// macOS Accessibility-first + Vision OCR fallback text extractor via Swift Helper IPC.
/// Helper strictly skips `AXSecureTextField` password controls.
/// </summary>
public sealed class TextExtractor(IMacHelperSupervisor supervisor) : ITextExtractor
{
    public CapabilityState AccessibilityCapability => MacHelperCapabilityEvaluator.EvaluateHelperCapability(
        supervisor,
        CapabilityKind.AccessibilityText,
        supervisor.Permissions.Accessibility,
        "macOS AXUIElement 树（排除安全输入框）",
        "请在「系统设置 > 隐私与安全性 > 辅助功能」中启用 AiAssistant。");

    public CapabilityState OcrCapability => MacHelperCapabilityEvaluator.EvaluateHelperCapability(
        supervisor,
        CapabilityKind.Ocr,
        requiredGrant: null,
        "macOS Vision 文字识别（简繁中文/英文）");

    public async Task<ExtractedTextResult> ExtractAsync(TextExtractionRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var pngBase64 = request.ScreenshotPngBytes is { Length: > 0 } bytes
            ? Convert.ToBase64String(bytes)
            : null;

        var response = await supervisor.InvokeAsync(
            new MacIpcRequest(
                string.Empty,
                MacHelperProtocol.Methods.ExtractText,
                WindowId: request.WindowHandle,
                PngBase64: pngBase64),
            ct);

        if (response.Ok && response.ExtractedText is not null && !string.IsNullOrWhiteSpace(response.ExtractedText.Text))
        {
            var method = string.Equals(response.ExtractedText.Source, "accessibility", StringComparison.OrdinalIgnoreCase)
                ? TextExtractionMethod.AccessibilityTree
                : string.Equals(response.ExtractedText.Source, "ocr", StringComparison.OrdinalIgnoreCase)
                    ? TextExtractionMethod.SystemOcr
                    : TextExtractionMethod.None;

            return new ExtractedTextResult(
                response.ExtractedText.Text.Trim(),
                method,
                Math.Clamp(response.ExtractedText.Confidence, 0.0, 1.0));
        }

        var fallback = string.IsNullOrWhiteSpace(request.WindowTitle)
            ? request.AppName
            : $"{request.AppName} - {request.WindowTitle}";

        return new ExtractedTextResult(fallback, TextExtractionMethod.None, 0.50);
    }
}

/// <summary>
/// macOS CoreAudio / AVAudioEngine 16kHz mono float32 capture adapter via Swift Helper IPC.
/// Feeds the unified S4 AudioHub so multiple consumers never contend for the microphone.
/// </summary>
public sealed class AudioCaptureFactory(IMacHelperSupervisor supervisor, IClock clock) : IAudioCaptureFactory
{
    public CapabilityState Capability => new AudioCapture(supervisor, clock).Capability;

    public IAudioCapture Create() => new AudioCapture(supervisor, clock);
}

/// <summary>macOS feedback playback is deferred to the S9 helper extension; voice text feedback remains available.</summary>
/// <summary>
/// macOS TTS feedback playback via the Swift Helper's AVAudioEngine (S7-R2).
/// Samples stay in memory as float32 PCM; no temporary audio files are written.
/// </summary>
public sealed class AudioPlayback(IMacHelperSupervisor supervisor) : IAudioPlayback
{
    private const int MaxDurationSeconds = 30;

    public CapabilityState Capability => supervisor.Status == MacHelperSupervisorStatus.Healthy
        ? CapabilityState.Supported(
            CapabilityKind.WakeWord,
            "AVAudioEngine 提示音播放（Swift Helper）")
        : CapabilityState.Degraded(
            CapabilityKind.WakeWord,
            $"macOS Helper {supervisor.Status}；语音反馈播放不可用",
            "语音命令仍可用；请在诊断页检查 Helper 状态。");

    public async Task PlayAsync(ReadOnlyMemory<float> samples, int sampleRateHz, CancellationToken ct)
    {
        if (samples.IsEmpty || sampleRateHz <= 0)
        {
            return;
        }

        // Feedback is short by design; clamp pathological payloads instead of blocking the session.
        var maxSamples = sampleRateHz * MaxDurationSeconds;
        if (samples.Length > maxSamples)
        {
            samples = samples[..maxSamples];
        }

        var pcmBytes = MemoryMarshal.AsBytes(samples.Span).ToArray();

        // Playback failures must never break a voice command; Capability already reflects helper health.
        await supervisor.InvokeAsync(
            new MacIpcRequest(
                string.Empty,
                MacHelperProtocol.Methods.PlayAudio,
                SampleRateHz: sampleRateHz,
                Float32PcmBase64: Convert.ToBase64String(pcmBytes)),
            ct);
    }
}

public sealed class AudioCapture(
    IMacHelperSupervisor supervisor,
    IClock clock,
    TimeSpan? pollInterval = null) : IAudioCapture
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);

    public CapabilityState Capability => MacHelperCapabilityEvaluator.EvaluateHelperCapability(
        supervisor,
        CapabilityKind.AudioCapture,
        supervisor.Permissions.Microphone,
        "CoreAudio / AVAudioEngine 16kHz 单声道（Swift Helper）",
        "请在「系统设置 > 隐私与安全性 > 麦克风」中启用 AiAssistant。");

    public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (supervisor.Status is MacHelperSupervisorStatus.Unavailable or MacHelperSupervisorStatus.ProtocolIncompatible)
            {
                yield break;
            }

            var response = await supervisor.InvokeAsync(
                new MacIpcRequest(string.Empty, MacHelperProtocol.Methods.PollAudioFrames),
                ct);

            if (!response.Ok || response.AudioChunk is null || string.IsNullOrWhiteSpace(response.AudioChunk.Float32PcmBase64))
            {
                continue;
            }

            byte[] pcmBytes;
            try
            {
                pcmBytes = Convert.FromBase64String(response.AudioChunk.Float32PcmBase64);
            }
            catch (FormatException)
            {
                continue;
            }

            if (pcmBytes.Length < sizeof(float))
            {
                continue;
            }

            var sampleCount = pcmBytes.Length / sizeof(float);
            var samples = new float[sampleCount];
            MemoryMarshal.Cast<byte, float>(pcmBytes.AsSpan(0, sampleCount * sizeof(float))).CopyTo(samples);

            yield return new AudioFrame(
                clock.UtcNow,
                response.AudioChunk.SampleRateHz > 0 ? response.AudioChunk.SampleRateHz : 16_000,
                response.AudioChunk.Channels > 0 ? response.AudioChunk.Channels : 1,
                samples);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
