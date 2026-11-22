using Microsoft.Extensions.Logging;

namespace AiAssistant.Platform.Linux.Portal;

/// <summary>
/// Coordinates the xdg ScreenCast lifecycle and rotates single-use restore tokens after every successful Start.
/// Authorization revocation closes only the portal session; the application UI and encrypted history remain available.
/// </summary>
public sealed class XdgPortalSessionManager(
    IXdgPortalClient client,
    IPortalRestoreTokenStore tokenStore,
    ILogger<XdgPortalSessionManager> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PortalScreenCastSession? _currentSession;
    private XdgPortalSessionSnapshot _snapshot = XdgPortalSessionSnapshot.Initial;
    private long _tokenRotationCount;
    private bool _disposed;

    public XdgPortalSessionSnapshot Snapshot => _snapshot;

    public async Task<PortalScreenCastSession?> EnsureSessionAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_currentSession is not null)
        {
            return _currentSession;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_currentSession is not null)
            {
                return _currentSession;
            }

            _snapshot = new XdgPortalSessionSnapshot(
                XdgPortalSessionStatus.AwaitingUser,
                "Waiting for xdg-desktop-portal screen source authorization.",
                0,
                _tokenRotationCount,
                null);

            var restoreToken = await tokenStore.LoadAsync(ct);
            var session = await TryCreateSessionAsync(restoreToken, ct);
            if (session is null && !string.IsNullOrEmpty(restoreToken) && _snapshot.Status != XdgPortalSessionStatus.PermissionRequired)
            {
                // Only restoration/transport failures retry. A user's explicit cancellation must never trigger a second dialog.
                await tokenStore.ClearAsync(ct);
                session = await TryCreateSessionAsync(null, ct);
            }

            if (session is null)
            {
                return null;
            }

            _currentSession = session;
            _snapshot = new XdgPortalSessionSnapshot(
                XdgPortalSessionStatus.Ready,
                session.RestoreTokenUsed
                    ? "已通过加密恢复令牌恢复 ScreenCast 会话。"
                    : "ScreenCast session authorized through xdg-desktop-portal.",
                session.Streams.Count,
                _tokenRotationCount,
                null);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InvalidateAsync(bool clearRestoreToken, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var previous = _currentSession;
            _currentSession = null;
            if (previous is not null)
            {
                try
                {
                    await client.CloseSessionAsync(previous.SessionPath, ct);
                }
                catch (Exception ex)
                {
                    logger.LogDebug("Portal session close skipped after {ExceptionType}.", ex.GetType().Name);
                }
            }

            if (clearRestoreToken)
            {
                await tokenStore.ClearAsync(ct);
            }

            _snapshot = new XdgPortalSessionSnapshot(
                clearRestoreToken ? XdgPortalSessionStatus.PermissionRequired : XdgPortalSessionStatus.NotRequested,
                clearRestoreToken ? "Portal 授权已撤销，需要重新授权。" : "Portal 会话已关闭。",
                0,
                _tokenRotationCount,
                null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var session = _currentSession;
        _currentSession = null;
        if (session is not null)
        {
            try
            {
                await client.CloseSessionAsync(session.SessionPath, CancellationToken.None);
            }
            catch
            {
                // Best-effort remote cleanup
            }
        }

        await client.DisposeAsync();
        _gate.Dispose();
    }

    private async Task<PortalScreenCastSession?> TryCreateSessionAsync(string? restoreToken, CancellationToken ct)
    {
        string? sessionPath = null;
        try
        {
            sessionPath = await client.CreateSessionAsync(ct);
            await client.SelectSourcesAsync(sessionPath, restoreToken, ct);
            var started = await client.StartAsync(sessionPath, ct);
            if (started.Streams.Count == 0)
            {
                throw new InvalidDataException("Portal Start returned no PipeWire streams.");
            }

            // Verify the scoped PipeWire remote can be opened before committing the next token to disk.
            using var remote = await client.OpenPipeWireRemoteAsync(sessionPath, ct);
            if (!string.IsNullOrWhiteSpace(started.NextRestoreToken))
            {
                await tokenStore.SaveAsync(started.NextRestoreToken, ct);
                Interlocked.Increment(ref _tokenRotationCount);
            }
            else if (!string.IsNullOrEmpty(restoreToken))
            {
                // The old token has been consumed even if the peer does not support issuing a replacement.
                await tokenStore.ClearAsync(ct);
            }

            return new PortalScreenCastSession(sessionPath, started.Streams, !string.IsNullOrEmpty(restoreToken));
        }
        catch (XdgPortalRequestException ex)
        {
            _snapshot = new XdgPortalSessionSnapshot(
                ex.ResponseCode == 1 ? XdgPortalSessionStatus.PermissionRequired : XdgPortalSessionStatus.Degraded,
                ex.ResponseCode == 1 ? "Portal 授权已取消或撤销。" : "Portal 请求未完成。",
                0,
                _tokenRotationCount,
                nameof(XdgPortalRequestException));
            if (sessionPath is not null)
            {
                try { await client.CloseSessionAsync(sessionPath, ct); } catch { }
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Portal session creation failed with {ExceptionType}.", ex.GetType().Name);
            _snapshot = new XdgPortalSessionSnapshot(
                XdgPortalSessionStatus.Degraded,
                "Portal service is unavailable or returned an unsupported response.",
                0,
                _tokenRotationCount,
                ex.GetType().Name);
            if (sessionPath is not null)
            {
                try { await client.CloseSessionAsync(sessionPath, ct); } catch { }
            }

            return null;
        }
    }
}
