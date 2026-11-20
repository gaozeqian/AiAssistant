using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Platform.Linux;

/// <summary>
/// Whitelisted file activity source using .NET FileSystemWatcher's inotify backend on Linux.
/// Only regular text/code files up to 200 KiB are read; hidden files and binary formats are ignored.
/// </summary>
public sealed class LinuxInotifyFileActivitySource(IClock clock) : IFileActivitySource
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".cs", ".swift", ".json", ".yaml", ".yml", ".xml", ".axaml", ".ts", ".tsx", ".js", ".py", ".sql", ".sln", ".csproj"
    };

    private volatile string? _lastErrorType;

    public SourceKind Kind => SourceKind.FileActivity;

    public CapabilityState Capability => !OperatingSystem.IsLinux()
        ? CapabilityState.Unavailable(CapabilityKind.FileActivity, "Linux inotify adapter is inactive on this host")
        : _lastErrorType is null
            ? CapabilityState.Supported(CapabilityKind.FileActivity, "inotify-backed watcher restricted to whitelisted directories")
            : CapabilityState.Degraded(CapabilityKind.FileActivity, $"inotify watcher degraded ({_lastErrorType})", "Check inotify watch limits and directory permissions.");

    public async IAsyncEnumerable<CapturedSample> CaptureAsync(CaptureContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var directories = context.WhitelistedDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (directories.Length == 0)
        {
            yield break;
        }

        var channel = Channel.CreateBounded<CapturedSample>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        var watchers = new List<FileSystemWatcher>();
        try
        {
            foreach (var directory in directories)
            {
                FileSystemWatcher watcher;
                try
                {
                    watcher = new FileSystemWatcher(directory)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                    };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _lastErrorType = ex.GetType().Name;
                    continue;
                }

                void Push(string fullPath, string operation)
                {
                    var fileName = Path.GetFileName(fullPath);
                    if (string.IsNullOrWhiteSpace(fileName) || fileName.StartsWith('.'))
                    {
                        return;
                    }

                    var sample = new CapturedSample(
                        clock.Now(),
                        SourceKind.FileActivity,
                        "inotify",
                        $"{operation}: {fileName}",
                        ExtractedText: TryReadSafeTextSnippet(fullPath, context.MaxTextFileBytes),
                        Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["operation"] = operation,
                            ["fileName"] = fileName,
                            ["extension"] = Path.GetExtension(fullPath)
                        });
                    channel.Writer.TryWrite(sample);
                }

                watcher.Created += (_, e) => Push(e.FullPath, "Created");
                watcher.Changed += (_, e) => Push(e.FullPath, "Modified");
                watcher.Renamed += (_, e) => Push(e.FullPath, "Renamed");
                watcher.Deleted += (_, e) => Push(e.FullPath, "Deleted");
                watcher.Error += (_, e) => _lastErrorType = e.GetException().GetType().Name;
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }

            if (watchers.Count == 0)
            {
                yield break;
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

    public static string? TryReadSafeTextSnippet(string fullPath, int maxBytes)
    {
        if (!AllowedExtensions.Contains(Path.GetExtension(fullPath)))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(fullPath);
            if (!file.Exists || file.Length <= 0 || file.Length > maxBytes)
            {
                return null;
            }

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[Math.Min(2048, maxBytes)];
            var count = reader.Read(buffer, 0, buffer.Length);
            return count > 0 ? new string(buffer, 0, count) : null;
        }
        catch
        {
            return null;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Lazy PulseAudio/PipeWire-compatible capture through libpulse-simple.
/// The microphone is opened only when a consumer subscribes; no resident audio stream is created at registration time.
/// </summary>
public sealed class LinuxAudioCaptureFactory(IClock clock) : IAudioCaptureFactory
{
    public CapabilityState Capability => new LinuxPulseAudioCapture(clock).Capability;

    public IAudioCapture Create() => new LinuxPulseAudioCapture(clock);
}

public sealed class LinuxPulseAudioCapture(IClock clock) : IAudioCapture
{
    private volatile string? _lastErrorType;

    public CapabilityState Capability => !OperatingSystem.IsLinux()
        ? CapabilityState.Unavailable(CapabilityKind.AudioCapture, "PulseAudio adapter is inactive on this host")
        : _lastErrorType is null
            ? CapabilityState.Supported(CapabilityKind.AudioCapture, "PulseAudio/PipeWire pulse-simple 16kHz mono on-demand capture")
            : CapabilityState.Degraded(CapabilityKind.AudioCapture, $"PulseAudio capture degraded ({_lastErrorType})", "Check PulseAudio or PipeWire pulse compatibility service.");

    public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        nint stream = 0;
        try
        {
            stream = OpenStream();
            if (stream == 0)
            {
                yield break;
            }

            while (!ct.IsCancellationRequested)
            {
                var frame = ReadFrame(stream, ct);
                if (frame is null)
                {
                    yield break;
                }

                yield return frame;
                await Task.Yield();
            }
        }
        finally
        {
            if (stream != 0)
            {
                PulseNative.pa_simple_free(stream);
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private nint OpenStream()
    {
        try
        {
            var spec = new PulseNative.pa_sample_spec { format = 3, rate = 16_000, channels = 1 }; // PA_SAMPLE_S16LE
            var stream = PulseNative.pa_simple_new(
                0,
                "AiAssistant",
                2, // PA_STREAM_RECORD
                0,
                "activity-audio",
                ref spec,
                0,
                0,
                out _);
            if (stream == 0)
            {
                _lastErrorType = "PulseOpenFailed";
            }

            return stream;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _lastErrorType = ex.GetType().Name;
            return 0;
        }
    }

    private AudioFrame? ReadFrame(nint stream, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var pcmBytes = new byte[320 * 2]; // 20ms at 16kHz
            if (PulseNative.pa_simple_read(stream, pcmBytes, (nuint)pcmBytes.Length, out _) != 0)
            {
                _lastErrorType = "PulseReadFailed";
                return null;
            }

            var pcm = MemoryMarshal.Cast<byte, short>(pcmBytes);
            var samples = new float[pcm.Length];
            for (var i = 0; i < pcm.Length; i++)
            {
                samples[i] = pcm[i] / 32768f;
            }

            _lastErrorType = null;
            return new AudioFrame(clock.UtcNow, 16_000, 1, samples);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _lastErrorType = ex.GetType().Name;
            return null;
        }
    }

    private static partial class PulseNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct pa_sample_spec
        {
            internal int format;
            internal uint rate;
            internal byte channels;
        }

        [DllImport("libpulse-simple.so.0", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        internal static extern nint pa_simple_new(
            nint server,
            string name,
            int direction,
            nint device,
            string streamName,
            ref pa_sample_spec sampleSpec,
            nint channelMap,
            nint bufferAttributes,
            out int error);

        [DllImport("libpulse-simple.so.0")]
        internal static extern int pa_simple_read(nint stream, byte[] data, nuint bytes, out int error);

        [DllImport("libpulse-simple.so.0")]
        internal static extern void pa_simple_free(nint stream);
    }
}
