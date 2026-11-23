using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;

namespace AiAssistant.Platform.macOS.Ipc;

public sealed class MacHelperOptions
{
    public string? HelperExecutablePath { get; init; }
    public string? SocketDirectory { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>
    /// The handshake performs a blocking TCC lookup (measured 1.5–1.9 s on macOS 26 when tccd is cold),
    /// so it gets a wider budget than ordinary requests instead of triggering a false restart.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);
}

public interface IMacHelperProcessInstance : IAsyncDisposable
{
    bool HasExited { get; }

    /// <summary>OS process id, when the launcher runs a real process (diagnostics and crash tests).</summary>
    int? ProcessId { get; }
}

public interface IMacHelperProcessLauncher
{
    Task<IMacHelperProcessInstance> LaunchAsync(string socketPath, string oneTimeAuthToken, CancellationToken ct);
}

/// <summary>
/// Launches the signed macOS Swift helper binary (`aiassistant-mac-helper`), passing the one-time
/// session authentication token via environment variable rather than process command-line arguments.
/// </summary>
public sealed class SubprocessMacHelperLauncher(string executablePath) : IMacHelperProcessLauncher
{
    public string ExecutablePath { get; } = executablePath;

    public Task<IMacHelperProcessInstance> LaunchAsync(string socketPath, string oneTimeAuthToken, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(ExecutablePath))
        {
            throw new FileNotFoundException("macOS Swift helper binary was not found.", ExecutablePath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };
        psi.ArgumentList.Add("--socket-path");
        psi.ArgumentList.Add(socketPath);
        psi.Environment[MacHelperProtocol.AuthTokenEnvVar] = oneTimeAuthToken;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start macOS Swift helper process.");

        return Task.FromResult<IMacHelperProcessInstance>(new OsProcessInstance(process));
    }

    private sealed class OsProcessInstance(Process process) : IMacHelperProcessInstance
    {
        public bool HasExited => process.HasExited;

        public int? ProcessId
        {
            get
            {
                try
                {
                    return process.HasExited ? null : process.Id;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await process.WaitForExitAsync(cts.Token);
                }
            }
            catch
            {
                // Best-effort process cleanup
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}

/// <summary>
/// Multiplexed AF_UNIX stream client handling concurrent request/response correlation and per-call timeouts.
/// </summary>
public sealed class UnixDomainSocketMacHelperClient : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<MacIpcResponse>> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _readerCts = new();
    private readonly Task _readerTask;
    private long _nextSeq;
    private volatile bool _faulted;

    private UnixDomainSocketMacHelperClient(Socket socket, NetworkStream stream)
    {
        _socket = socket;
        _stream = stream;
        _readerTask = Task.Run(() => RunReadLoopAsync(_readerCts.Token), CancellationToken.None);
    }

    public bool IsConnected => !_faulted && _socket.Connected;

    public event EventHandler? Disconnected;

    public static async Task<UnixDomainSocketMacHelperClient> ConnectAsync(
        string socketPath,
        TimeSpan connectTimeout,
        CancellationToken ct)
    {
        var deadline = TimeProvider.System.GetUtcNow().UtcDateTime.Add(connectTimeout);
        Exception? lastError = null;

        while (TimeProvider.System.GetUtcNow().UtcDateTime < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromMilliseconds(250));
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), attemptCts.Token);
                var stream = new NetworkStream(socket, ownsSocket: true);
                return new UnixDomainSocketMacHelperClient(socket, stream);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastError = ex;
                socket.Dispose();
                await Task.Delay(30, ct);
            }
        }

        throw new TimeoutException($"Timed out waiting for macOS helper Unix socket '{Path.GetFileName(socketPath)}'.", lastError);
    }

    public async Task<MacIpcResponse> SendAsync(MacIpcRequest request, TimeSpan timeout, CancellationToken ct)
    {
        if (_faulted)
        {
            return new MacIpcResponse(request.RequestId, Ok: false, ErrorCode: MacHelperProtocol.ErrorCodes.Disconnected);
        }

        var reqId = string.IsNullOrWhiteSpace(request.RequestId)
            ? $"req_{Interlocked.Increment(ref _nextSeq).ToString(CultureInfo.InvariantCulture)}"
            : request.RequestId;
        var outgoing = request with { RequestId = reqId };

        var tcs = new TaskCompletionSource<MacIpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[reqId] = tcs;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await _writeLock.WaitAsync(timeoutCts.Token);
            try
            {
                await MacHelperFrameCodec.WriteFrameAsync(_stream, outgoing, timeoutCts.Token);
            }
            finally
            {
                _writeLock.Release();
            }

            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new MacIpcResponse(reqId, Ok: false, ErrorCode: MacHelperProtocol.ErrorCodes.Timeout, ErrorMessage: "IPC call timed out.");
        }
        finally
        {
            _pending.TryRemove(reqId, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _faulted = true;
        await _readerCts.CancelAsync();
        try
        {
            _stream.Dispose();
        }
        catch
        {
            // Ignore close errors
        }

        try
        {
            await _readerTask;
        }
        catch
        {
            // Expected on socket close
        }

        FailAllPending();
        _writeLock.Dispose();
        _readerCts.Dispose();
    }

    private async Task RunReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var response = await MacHelperFrameCodec.ReadFrameAsync<MacIpcResponse>(_stream, ct);
                if (response is null)
                {
                    break;
                }

                if (!string.IsNullOrEmpty(response.RequestId) &&
                    _pending.TryRemove(response.RequestId, out var waiter))
                {
                    waiter.TrySetResult(response);
                }
            }
        }
        catch
        {
            // Peer crashed or closed socket
        }
        finally
        {
            _faulted = true;
            FailAllPending();
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private void FailAllPending()
    {
        foreach (var kv in _pending)
        {
            if (_pending.TryRemove(kv.Key, out var waiter))
            {
                waiter.TrySetResult(new MacIpcResponse(
                    kv.Key,
                    Ok: false,
                    ErrorCode: MacHelperProtocol.ErrorCodes.Disconnected,
                    ErrorMessage: "macOS helper connection closed."));
            }
        }
    }
}
