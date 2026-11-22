using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Platform.macOS.Ipc;

public enum MacHelperSupervisorStatus
{
    Stopped = 0,
    Starting = 1,
    Healthy = 2,
    BackoffRestarting = 3,
    ProtocolIncompatible = 4,
    Unavailable = 5
}

public interface IMacHelperSupervisor : IAsyncDisposable
{
    MacHelperSupervisorStatus Status { get; }
    string StatusSummary { get; }
    long RestartCount { get; }
    MacTccPermissionSnapshot Permissions { get; }

    Task StartAsync(CancellationToken ct);
    Task<MacIpcResponse> InvokeAsync(MacIpcRequest request, CancellationToken ct);
}

/// <summary>
/// Supervises the macOS Swift helper process over AF_UNIX IPC with versioned handshake,
/// one-time session token authentication, periodic heartbeat, and exponential backoff restart on crash (ADR-0005).
/// </summary>
public sealed class MacHelperSupervisor : IMacHelperSupervisor
{
    private readonly IMacHelperProcessLauncher? _launcher;
    private readonly MacHelperOptions _options;
    private readonly ILogger<MacHelperSupervisor> _logger;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();

    private IMacHelperProcessInstance? _currentProcess;
    private UnixDomainSocketMacHelperClient? _currentClient;
    private string? _currentSocketPath;
    private Task? _supervisorTask;

    private volatile MacHelperSupervisorStatus _status = MacHelperSupervisorStatus.Stopped;
    private volatile string _statusSummary = "macOS helper not started";
    private volatile MacTccPermissionSnapshot _permissions = MacTccPermissionSnapshot.AllUnknown;
    private long _restartCount;
    private bool _started;

    public MacHelperSupervisor(
        IMacHelperProcessLauncher? launcher,
        MacHelperOptions options,
        ILogger<MacHelperSupervisor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _launcher = launcher;
        _options = options;
        _logger = logger;
    }

    public MacHelperSupervisorStatus Status => _status;
    public string StatusSummary => _statusSummary;
    public long RestartCount => Interlocked.Read(ref _restartCount);
    public MacTccPermissionSnapshot Permissions => _permissions;

    public async Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_started)
        {
            return;
        }

        _started = true;
        if (_launcher is null)
        {
            _status = MacHelperSupervisorStatus.Unavailable;
            _statusSummary = "macOS Swift helper binary is not bundled or unavailable on this host.";
            return;
        }

        _status = MacHelperSupervisorStatus.Starting;
        var connected = await TryEstablishSessionOnceAsync(ct);
        if (!connected && _status != MacHelperSupervisorStatus.ProtocolIncompatible)
        {
            _status = MacHelperSupervisorStatus.BackoffRestarting;
        }

        _supervisorTask = Task.Run(() => RunSupervisionLoopAsync(_shutdownCts.Token), CancellationToken.None);
    }

    public async Task<MacIpcResponse> InvokeAsync(MacIpcRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var client = _currentClient;
        if (_status != MacHelperSupervisorStatus.Healthy || client is null || !client.IsConnected)
        {
            return new MacIpcResponse(
                request.RequestId,
                Ok: false,
                ErrorCode: MacHelperProtocol.ErrorCodes.Disconnected,
                ErrorMessage: _statusSummary);
        }

        var response = await client.SendAsync(request, _options.RequestTimeout, ct);
        if (response.Permissions is not null)
        {
            _permissions = response.Permissions.ToDomainSnapshot();
        }

        return response;
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdownCts.CancelAsync();
        if (_supervisorTask is not null)
        {
            try
            {
                await _supervisorTask;
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
        }

        await TearDownCurrentSessionAsync(sendGracefulShutdown: true);
        _status = MacHelperSupervisorStatus.Stopped;
        _sessionGate.Dispose();
        _shutdownCts.Dispose();
    }

    private async Task RunSupervisionLoopAsync(CancellationToken ct)
    {
        var consecutiveFailures = _status == MacHelperSupervisorStatus.Healthy ? 0 : 1;

        while (!ct.IsCancellationRequested)
        {
            // Terminal states do not spin-restart
            if (_status is MacHelperSupervisorStatus.ProtocolIncompatible or MacHelperSupervisorStatus.Unavailable)
            {
                return;
            }

            if (_status == MacHelperSupervisorStatus.Healthy &&
                _currentClient is { IsConnected: true } activeClient &&
                (_currentProcess is null || !_currentProcess.HasExited))
            {
                await Task.Delay(_options.HeartbeatInterval, ct);
                var ping = await activeClient.SendAsync(
                    new MacIpcRequest(string.Empty, MacHelperProtocol.Methods.Ping),
                    _options.RequestTimeout,
                    ct);

                if (ping.Ok)
                {
                    if (ping.Permissions is not null)
                    {
                        _permissions = ping.Permissions.ToDomainSnapshot();
                    }

                    consecutiveFailures = 0;
                    continue;
                }

                _logger.LogWarning("macOS helper heartbeat failed ({ErrorCode}); scheduling restart.", ping.ErrorCode ?? "unknown");
            }

            // Connection lost or process crashed -> exponential backoff restart
            _status = MacHelperSupervisorStatus.BackoffRestarting;
            var backoff = ComputeBackoffDelay(consecutiveFailures);
            _statusSummary = $"macOS helper disconnected; restarting in {backoff.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms.";

            await Task.Delay(backoff, ct);

            Interlocked.Increment(ref _restartCount);
            var recovered = await TryEstablishSessionOnceAsync(ct);
            if (recovered)
            {
                consecutiveFailures = 0;
                _logger.LogInformation("macOS helper recovered cleanly (restarts={RestartCount}).", RestartCount);
            }
            else
            {
                consecutiveFailures++;
            }
        }
    }

    private async Task<bool> TryEstablishSessionOnceAsync(CancellationToken ct)
    {
        if (_launcher is null)
        {
            return false;
        }

        await _sessionGate.WaitAsync(ct);
        try
        {
            await TearDownCurrentSessionAsync(sendGracefulShutdown: false);

            var socketPath = BuildShortSocketPath();
            DeleteSocketFileQuietly(socketPath);
            var oneTimeToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

            _currentSocketPath = socketPath;
            _currentProcess = await _launcher.LaunchAsync(socketPath, oneTimeToken, ct);
            _currentClient = await UnixDomainSocketMacHelperClient.ConnectAsync(socketPath, _options.ConnectTimeout, ct);

            var handshake = await _currentClient.SendAsync(
                new MacIpcRequest(
                    "hs_1",
                    MacHelperProtocol.Methods.Handshake,
                    ProtocolVersion: MacHelperProtocol.CurrentVersion,
                    AuthToken: oneTimeToken),
                _options.HandshakeTimeout,
                ct);

            if (!handshake.Ok)
            {
                if (string.Equals(handshake.ErrorCode, MacHelperProtocol.ErrorCodes.UnsupportedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    _status = MacHelperSupervisorStatus.ProtocolIncompatible;
                    _statusSummary = $"macOS helper protocol mismatch (peer v{handshake.ProtocolVersion?.ToString(CultureInfo.InvariantCulture) ?? "?"}, expected v{MacHelperProtocol.CurrentVersion}).";
                    await TearDownCurrentSessionAsync(sendGracefulShutdown: false);
                    return false;
                }

                _statusSummary = $"macOS helper handshake rejected ({handshake.ErrorCode ?? "error"}).";
                await TearDownCurrentSessionAsync(sendGracefulShutdown: false);
                return false;
            }

            if (handshake.ProtocolVersion != MacHelperProtocol.CurrentVersion)
            {
                _status = MacHelperSupervisorStatus.ProtocolIncompatible;
                _statusSummary = $"macOS helper reported incompatible protocol v{handshake.ProtocolVersion?.ToString(CultureInfo.InvariantCulture) ?? "?"}, expected v{MacHelperProtocol.CurrentVersion}.";
                await TearDownCurrentSessionAsync(sendGracefulShutdown: false);
                return false;
            }

            _permissions = handshake.Permissions?.ToDomainSnapshot() ?? MacTccPermissionSnapshot.AllUnknown;
            _status = MacHelperSupervisorStatus.Healthy;
            _statusSummary = $"macOS Swift helper active (IPC v{MacHelperProtocol.CurrentVersion})";
            return true;
        }
        catch (FileNotFoundException)
        {
            _status = MacHelperSupervisorStatus.Unavailable;
            _statusSummary = "macOS Swift helper binary not found.";
            await TearDownCurrentSessionAsync(sendGracefulShutdown: false);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TearDownCurrentSessionAsync(sendGracefulShutdown: false);
            throw;
        }
        catch (Exception ex)
        {
            _statusSummary = $"macOS helper startup failed ({ex.GetType().Name}).";
            _logger.LogWarning("macOS helper startup attempt failed with {ExceptionType}.", ex.GetType().Name);
            await TearDownCurrentSessionAsync(sendGracefulShutdown: false);
            return false;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private TimeSpan ComputeBackoffDelay(int failureIndex)
    {
        var shift = Math.Clamp(failureIndex, 0, 10);
        var ms = _options.InitialBackoff.TotalMilliseconds * (1 << shift);
        var clampedMs = Math.Min(ms, _options.MaxBackoff.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Max(10, clampedMs));
    }

    private string BuildShortSocketPath()
    {
        // Keep AF_UNIX sun_path strictly <= 96 UTF-8 bytes so both macOS (104B) and Windows/Linux (108B) succeed!
        var shortId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var fileName = $"aia-{shortId}.sock";

        if (!string.IsNullOrWhiteSpace(_options.SocketDirectory))
        {
            var candidateDir = _options.SocketDirectory!;
            var candidatePath = Path.Combine(candidateDir, fileName);
            if (System.Text.Encoding.UTF8.GetByteCount(candidatePath) <= 96)
            {
                Directory.CreateDirectory(candidateDir);
                return candidatePath;
            }
        }

        var fallbackDir = OperatingSystem.IsMacOS() ? "/tmp" : Path.GetTempPath();
        Directory.CreateDirectory(fallbackDir);
        return Path.Combine(fallbackDir, fileName);
    }

    private async Task TearDownCurrentSessionAsync(bool sendGracefulShutdown)
    {
        if (_currentClient is not null)
        {
            if (sendGracefulShutdown && _currentClient.IsConnected)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                    _ = await _currentClient.SendAsync(
                        new MacIpcRequest("bye", MacHelperProtocol.Methods.Shutdown),
                        TimeSpan.FromMilliseconds(250),
                        cts.Token);
                }
                catch
                {
                    // Ignore shutdown send errors
                }
            }

            await _currentClient.DisposeAsync();
            _currentClient = null;
        }

        if (_currentProcess is not null)
        {
            await _currentProcess.DisposeAsync();
            _currentProcess = null;
        }

        if (!string.IsNullOrEmpty(_currentSocketPath))
        {
            DeleteSocketFileQuietly(_currentSocketPath);
            _currentSocketPath = null;
        }
    }

    private static void DeleteSocketFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup failure
        }
    }
}
