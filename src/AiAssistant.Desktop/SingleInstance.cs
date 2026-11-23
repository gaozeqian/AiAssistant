using System.Net.Sockets;
using System.Text;

namespace AiAssistant.Desktop;

/// <summary>
/// Single-instance activation over a Unix domain socket next to the encrypted store. Closing the
/// window only hides it (capture keeps running), so launching the app again must bring the
/// existing window back instead of starting a second recorder.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string ActivationMessage = "show";

    private readonly string _socketPath;
    private readonly Lock _sync = new();
    private Socket? _listener;
    private CancellationTokenSource? _shutdown;
    private bool _disposed;

    public SingleInstance(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        _socketPath = socketPath;
    }

    /// <summary>Returns true when an already-running instance accepted the activation request.</summary>
    public static bool TryActivateExisting(string socketPath)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return false;
        }

        if (!File.Exists(socketPath))
        {
            return false;
        }

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            socket.Send(Encoding.UTF8.GetBytes(ActivationMessage));
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException or ArgumentException)
        {
            // Stale socket file from a crashed instance: the caller will start normally.
            return false;
        }
    }

    /// <summary>Starts listening; <paramref name="onActivate"/> runs on a background thread.</summary>
    public void Start(Action onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed || _listener is not null)
            {
                return;
            }

            try
            {
                if (File.Exists(_socketPath))
                {
                    File.Delete(_socketPath);
                }

                var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
                listener.Listen(4);
                _listener = listener;
                _shutdown = new CancellationTokenSource();
            }
            catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
            {
                _listener = null;
                return;
            }
        }

        _ = AcceptLoopAsync(onActivate, _shutdown!.Token);
    }

    private async Task AcceptLoopAsync(Action onActivate, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener!.AcceptAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            client.Dispose();
            try
            {
                onActivate();
            }
            catch (Exception ex)
            {
                // Activation must never crash the capture loop.
                AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Desktop.SingleInstance.AcceptLoopAsync", ex);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown?.Cancel();
            _shutdown?.Dispose();
            _shutdown = null;
            _listener?.Dispose();
            _listener = null;
        }

        try
        {
            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }
        }
        catch (IOException ex)
        {
            // 清理遗留 socket 文件失败属预期，忽略即可。
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Desktop.SingleInstance.Dispose", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            // 无权限删除 socket 文件属预期，忽略即可。
            AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("Desktop.SingleInstance.Dispose", ex);
        }
    }
}
