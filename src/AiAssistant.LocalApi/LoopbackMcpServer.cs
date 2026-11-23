using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AiAssistant.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiAssistant.LocalApi;

/// <summary>
/// Read-only MCP endpoint bound strictly to 127.0.0.1. Transport parsing lives in
/// <see cref="McpHttpTransport"/> and security checks in <see cref="McpRequestAuthenticator"/>;
/// this type owns listener/session lifecycle and request metrics only.
/// Disabled by default; the Host only starts it when explicitly enabled.
/// </summary>
public sealed class LoopbackMcpServer : IAsyncDisposable
{
    private const string McpPath = "/mcp";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly McpServerOptions _options;
    private readonly LocalMcpTokenStore _tokens;
    private readonly McpJsonRpcDispatcher _dispatcher;
    private readonly McpRequestAuthenticator _authenticator;
    private readonly ILogger<LoopbackMcpServer> _logger;
    private readonly Lock _sync = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _runCts;
    private Task? _acceptTask;
    private string? _endpoint;
    private long _requests;
    private long _unauthorized;
    private long _rateLimited;
    private long _toolCalls;
    private bool _disposed;

    public LoopbackMcpServer(
        McpServerOptions options,
        LocalMcpTokenStore tokens,
        McpToolCatalog tools,
        IClock clock,
        ILogger<LoopbackMcpServer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _tokens = tokens;
        _dispatcher = new McpJsonRpcDispatcher(tools);
        _authenticator = new McpRequestAuthenticator(options, clock);
        _logger = logger;
    }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _listener is not null;
            }
        }
    }

    public int Port { get; private set; }

    public string? Endpoint
    {
        get
        {
            lock (_sync)
            {
                return _endpoint;
            }
        }
    }

    public LocalMcpSnapshot Status => new(
        _options.Enabled,
        IsRunning,
        Port,
        Endpoint,
        Interlocked.Read(ref _requests),
        Interlocked.Read(ref _unauthorized),
        Interlocked.Read(ref _rateLimited),
        Interlocked.Read(ref _toolCalls),
        File.Exists(_tokens.FilePath));

    public static bool IsLoopbackOnly(IPAddress address) => McpRequestAuthenticator.IsLoopbackOnly(address);

    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_options.Enabled)
        {
            _logger.LogInformation("Local read-only MCP is disabled; no socket was bound.");
            return;
        }

        lock (_sync)
        {
            if (_listener is not null)
            {
                return;
            }
        }

        var token = await _tokens.GetOrCreateAsync(ct);
        var listener = new TcpListener(IPAddress.Loopback, _options.Port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex) when (_options.Port != 0)
        {
            _logger.LogWarning(
                "Local MCP port {Port} unavailable ({SocketError}); falling back to an ephemeral loopback port.",
                _options.Port,
                ex.SocketErrorCode);
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
        }

        var cts = new CancellationTokenSource();
        var acceptTask = Task.Run(() => AcceptLoopAsync(listener, cts.Token), CancellationToken.None);
        lock (_sync)
        {
            _listener = listener;
            _runCts = cts;
            _acceptTask = acceptTask;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _endpoint = string.Create(
                CultureInfo.InvariantCulture,
                $"http://127.0.0.1:{Port}{McpPath}");
        }

        _authenticator.OnStarted(token, Port);
        _logger.LogInformation(
            "Local read-only MCP listening on {Endpoint}; tools={ToolCount}.",
            Endpoint,
            McpToolCatalog.Tools.Count);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        Task? acceptTask;
        lock (_sync)
        {
            listener = _listener;
            cts = _runCts;
            acceptTask = _acceptTask;
            _listener = null;
            _runCts = null;
            _acceptTask = null;
            _endpoint = null;
        }

        _authenticator.OnStopped();
        if (listener is null)
        {
            return;
        }

        if (cts is not null)
        {
            await cts.CancelAsync();
        }

        listener.Stop();
        if (acceptTask is not null)
        {
            try
            {
                await acceptTask.WaitAsync(ct);
            }
            catch (OperationCanceledException ex)
            {
                // Expected during shutdown
                AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("LocalApi.LoopbackMcpServer.StopAsync", ex);
            }
        }

        cts?.Dispose();
        _logger.LogInformation("Local read-only MCP stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync(CancellationToken.None);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug("Local MCP accept loop ended with {SocketError}.", ex.SocketErrorCode);
                break;
            }

            _ = HandleClientSafelyAsync(client, ct);
        }
    }

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                await HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException ex)
            {
                // Shutdown or request timeout
                AiAssistant.Application.Diagnostics.SuppressedExceptionLog.Trace("LocalApi.LoopbackMcpServer.HandleClientSafelyAsync", ex);
            }
            catch (IOException ex)
            {
                _logger.LogDebug("Local MCP connection dropped ({ExceptionType}).", ex.GetType().Name);
            }
            catch (SocketException ex)
            {
                _logger.LogDebug("Local MCP socket error {SocketError}.", ex.SocketErrorCode);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        if (remote is null || !McpRequestAuthenticator.IsLoopbackOnly(remote.Address))
        {
            _logger.LogWarning("Rejected non-loopback local MCP connection.");
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        var token = timeout.Token;
        var stream = client.GetStream();
        var outcome = await McpHttpTransport.ReadRequestAsync(stream, _options.MaxBodyBytes, token);
        if (outcome.Request is null)
        {
            await McpHttpTransport.WriteResponseAsync(stream, outcome.StatusCode, outcome.Error, token);
            return;
        }

        Interlocked.Increment(ref _requests);
        var request = outcome.Request;

        // MCP Streamable HTTP 安全规范：先校验来源头，再校验路径/方法，最后鉴权与限流。
        // 未授权请求不得消耗限流配额，否则本地攻击者可用伪造请求造成 DoS。
        if (!_authenticator.IsTrustedTransportHeaders(request.Headers))
        {
            Interlocked.Increment(ref _unauthorized);
            await McpHttpTransport.WriteResponseAsync(stream, 403, "Forbidden", token);
            return;
        }

        var isPost = string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase);
        var isDelete = string.Equals(request.Method, "DELETE", StringComparison.OrdinalIgnoreCase);
        if (!isPost && !isDelete)
        {
            await McpHttpTransport.WriteResponseAsync(stream, 405, "Method Not Allowed", token, "Allow: POST, DELETE\r\n");
            return;
        }

        if (!string.Equals(request.Path, McpPath, StringComparison.Ordinal))
        {
            await McpHttpTransport.WriteResponseAsync(stream, 404, "Not Found", token);
            return;
        }

        if (!_authenticator.IsAuthorized(request.Headers))
        {
            Interlocked.Increment(ref _unauthorized);
            await McpHttpTransport.WriteResponseAsync(stream, 401, "Unauthorized", token, "WWW-Authenticate: Bearer\r\n");
            return;
        }

        if (isPost && !McpRequestAuthenticator.IsSupportedProtocolHeader(request.Headers, out var versionError))
        {
            await McpHttpTransport.WriteResponseAsync(stream, 400, "Bad Request", token, json: versionError);
            return;
        }

        if (!_authenticator.TryAcquireRateLimit(out var retryAfter))
        {
            Interlocked.Increment(ref _rateLimited);
            await McpHttpTransport.WriteResponseAsync(stream, 429, "Too Many Requests", token, $"Retry-After: {retryAfter}\r\n");
            return;
        }

        if (isDelete)
        {
            // Streamable HTTP clients may terminate a session explicitly; this server is stateless.
            await McpHttpTransport.WriteResponseAsync(stream, 204, "No Content", token);
            return;
        }

        var dispatch = await _dispatcher.DispatchAsync(request.Body, token);
        if (dispatch.ToolInvoked)
        {
            Interlocked.Increment(ref _toolCalls);
        }

        var sessionId = dispatch.SessionId;
        if (sessionId is null &&
            request.Headers.TryGetValue("Mcp-Session-Id", out var incomingSession) &&
            !string.IsNullOrWhiteSpace(incomingSession))
        {
            sessionId = incomingSession.Trim();
        }

        if (dispatch.Json is null)
        {
            await McpHttpTransport.WriteResponseAsync(stream, 202, "Accepted", token, sessionId: sessionId);
            return;
        }

        if (McpHttpTransport.PrefersEventStream(request.Headers))
        {
            var sse = $"event: message\ndata: {dispatch.Json}\n\n";
            await McpHttpTransport.WriteResponseAsync(
                stream,
                200,
                "OK",
                token,
                json: sse,
                contentType: "text/event-stream; charset=utf-8",
                sessionId: sessionId);
            return;
        }

        await McpHttpTransport.WriteResponseAsync(
            stream,
            200,
            "OK",
            token,
            json: dispatch.Json,
            contentType: "application/json; charset=utf-8",
            sessionId: sessionId);
    }
}
