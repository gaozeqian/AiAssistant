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
