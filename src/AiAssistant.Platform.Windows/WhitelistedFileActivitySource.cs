using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Platform.Windows;

/// <summary>
/// Monitors ONLY user-whitelisted directories and reads text snippets strictly within size &amp; extension limits (design.md §3.2).
/// </summary>
public sealed class WhitelistedFileActivitySource(IClock clock) : IFileActivitySource
{
    private static readonly HashSet<string> AllowedTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".cs", ".json", ".yaml", ".yml", ".xml", ".axaml", ".ts", ".tsx", ".js", ".py", ".sql", ".sln", ".csproj"
    };

    public SourceKind Kind => SourceKind.FileActivity;

    public CapabilityState Capability =>
        CapabilityState.Supported(CapabilityKind.FileActivity, "白名单目录 FileSystemWatcher（仅 ≤200KB 文本/代码）");

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
