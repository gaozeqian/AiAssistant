using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AiAssistant.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiAssistant.LocalApi;

/// <summary>
/// Read-only MCP endpoint bound strictly to 127.0.0.1. Requests must carry the locally stored bearer token,
/// are rate limited, and can only reach the four read-only tools in <see cref="McpToolCatalog"/>.
/// Disabled by default; the Host only starts it when explicitly enabled.
/// </summary>
public sealed class LoopbackMcpServer : IAsyncDisposable
{
    private const string McpPath = "/mcp";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly McpServerOptions _options;
    private readonly LocalMcpTokenStore _tokens;
    private readonly McpJsonRpcDispatcher _dispatcher;
    private readonly SlidingWindowRateLimiter _limiter;
    private readonly ILogger<LoopbackMcpServer> _logger;
    private readonly Lock _sync = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _runCts;
    private Task? _acceptTask;
    private string? _token;
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
        ILogger<LoopbackMcpServer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _tokens = tokens;
        _dispatcher = new McpJsonRpcDispatcher(tools);
        _limiter = new SlidingWindowRateLimiter(options.MaxRequestsPerMinute);
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

    public static bool IsLoopbackOnly(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return IPAddress.IsLoopback(candidate);
    }

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
            _token = token;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _endpoint = string.Create(
                CultureInfo.InvariantCulture,
                $"http://127.0.0.1:{Port}{McpPath}");
        }

        _limiter.Reset();
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
            _token = null;
            _endpoint = null;
        }

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
            catch (OperationCanceledException)
            {
                // Expected during shutdown
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
            catch (OperationCanceledException)
            {
                // Shutdown or request timeout
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
        if (remote is null || !IsLoopbackOnly(remote.Address))
        {
            _logger.LogWarning("Rejected non-loopback local MCP connection.");
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        var token = timeout.Token;
        var stream = client.GetStream();
        var outcome = await ReadRequestAsync(stream, _options.MaxBodyBytes, token);
        if (outcome.Request is null)
        {
            await WriteResponseAsync(stream, outcome.StatusCode, outcome.Error, token);
            return;
        }

        Interlocked.Increment(ref _requests);
        var request = outcome.Request;

        if (!_limiter.TryAcquire(DateTimeOffset.UtcNow, out var retryAfter))
        {
            Interlocked.Increment(ref _rateLimited);
            await WriteResponseAsync(stream, 429, "Too Many Requests", token, $"Retry-After: {retryAfter}\r\n");
            return;
        }

        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            await WriteResponseAsync(stream, 405, "Method Not Allowed", token, "Allow: POST\r\n");
            return;
        }

        if (!string.Equals(request.Path, McpPath, StringComparison.Ordinal))
        {
            await WriteResponseAsync(stream, 404, "Not Found", token);
            return;
        }

        if (!IsAuthorized(request.Headers))
        {
            Interlocked.Increment(ref _unauthorized);
            await WriteResponseAsync(stream, 401, "Unauthorized", token, "WWW-Authenticate: Bearer\r\n");
            return;
        }

        var dispatch = await _dispatcher.DispatchAsync(request.Body, token);
        if (dispatch.ToolInvoked)
        {
            Interlocked.Increment(ref _toolCalls);
        }

        if (dispatch.Json is null)
        {
            await WriteResponseAsync(stream, 202, "Accepted", token);
            return;
        }

        await WriteResponseAsync(stream, 200, "OK", token, json: dispatch.Json);
    }

    private bool IsAuthorized(Dictionary<string, string> headers)
    {
        var expected = _token;
        if (string.IsNullOrEmpty(expected) ||
            !headers.TryGetValue("Authorization", out var header) ||
            string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var provided = header[scheme.Length..].Trim();
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return providedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    private static async Task<HttpReadOutcome> ReadRequestAsync(
        NetworkStream stream,
        int maxBodyBytes,
        CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0)
            {
                return new HttpReadOutcome(null, 400, "Bad Request");
            }

            buffer.Write(chunk, 0, read);
            if (buffer.Length > 32 * 1024)
            {
                return new HttpReadOutcome(null, 431, "Request Header Fields Too Large");
            }

            headerEnd = FindHeaderEnd(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        }

        var headerText = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2)
        {
            return new HttpReadOutcome(null, 400, "Bad Request");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var separator = lines[i].IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            headers[lines[i][..separator].Trim()] = lines[i][(separator + 1)..].Trim();
        }

        var bodyLength = 0;
        if (headers.TryGetValue("Content-Length", out var contentLength))
        {
            if (!int.TryParse(contentLength, NumberStyles.Integer, CultureInfo.InvariantCulture, out bodyLength) ||
                bodyLength < 0)
            {
                return new HttpReadOutcome(null, 400, "Bad Request");
            }
        }

        if (headers.ContainsKey("Transfer-Encoding"))
        {
            return new HttpReadOutcome(null, 411, "Length Required");
        }

        if (bodyLength > maxBodyBytes)
        {
            return new HttpReadOutcome(null, 413, "Payload Too Large");
        }

        var bodyStart = headerEnd + 4;
        var body = new byte[bodyLength];
        var available = (int)buffer.Length - bodyStart;
        var copied = Math.Clamp(available, 0, bodyLength);
        if (copied > 0)
        {
            buffer.GetBuffer().AsSpan(bodyStart, copied).CopyTo(body);
        }

        while (copied < bodyLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(copied, bodyLength - copied), ct);
            if (read == 0)
            {
                return new HttpReadOutcome(null, 400, "Bad Request");
            }

            copied += read;
        }

        return new HttpReadOutcome(
            new HttpRequestEnvelope(requestLine[0], requestLine[1], headers, Encoding.UTF8.GetString(body)),
            200,
            string.Empty);
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == (byte)'\r' && data[i + 1] == (byte)'\n' &&
                data[i + 2] == (byte)'\r' && data[i + 3] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reason,
        CancellationToken ct,
        string? extraHeaders = null,
        string? json = null)
    {
        var body = json is null ? [] : Encoding.UTF8.GetBytes(json);
        var builder = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {statusCode} {reason}\r\n")
            .Append("Content-Type: application/json; charset=utf-8\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n");
        if (!string.IsNullOrEmpty(extraHeaders))
        {
            builder.Append(extraHeaders);
        }

        builder.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), ct);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, ct);
        }

        await stream.FlushAsync(ct);
    }

    private sealed record HttpRequestEnvelope(
        string Method,
        string Path,
        Dictionary<string, string> Headers,
        string Body);

    private readonly record struct HttpReadOutcome(HttpRequestEnvelope? Request, int StatusCode, string Error);
}
