using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AiAssistant.Application.Ports;

namespace AiAssistant.LocalApi;

/// <summary>
/// Transport-level security for the loopback MCP endpoint: loopback-only peers, Host/Origin
/// allow-listing, bearer-token authentication, protocol-version validation and rate limiting.
/// </summary>
internal sealed class McpRequestAuthenticator
{
    private readonly McpServerOptions _options;
    private readonly SlidingWindowRateLimiter _limiter;
    private readonly IClock _clock;
    private string? _token;

    public McpRequestAuthenticator(McpServerOptions options, IClock clock)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _limiter = new SlidingWindowRateLimiter(options.MaxRequestsPerMinute);
    }

    public int Port { get; private set; }

    public void OnStarted(string token, int port)
    {
        _token = token;
        Port = port;
        _limiter.Reset();
    }

    public void OnStopped() => _token = null;

    public static bool IsLoopbackOnly(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return IPAddress.IsLoopback(candidate);
    }

    /// <summary>
    /// MCP Streamable HTTP: reject cross-origin/DNS-rebinding probes and non-loopback Host headers.
    /// The Origin header is optional for CLI clients; when present it must name this loopback endpoint.
    /// </summary>
    public bool IsTrustedTransportHeaders(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Host", out var host) || !IsLoopbackAuthority(host, Port))
        {
            return false;
        }

        if (headers.TryGetValue("Origin", out var origin) && !string.IsNullOrWhiteSpace(origin))
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !IsLoopbackAuthority(uri.Authority, Port))
            {
                return false;
            }
        }

        return true;
    }

    public bool IsAuthorized(Dictionary<string, string> headers)
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

    public static bool IsSupportedProtocolHeader(Dictionary<string, string> headers, out string? error)
    {
        error = null;
        if (!headers.TryGetValue("MCP-Protocol-Version", out var value) || string.IsNullOrWhiteSpace(value))
        {
            // Clients older than 2025-06-18 did not send this header; accept as 2025-03-26.
            return true;
        }

        if (McpJsonRpcDispatcher.IsSupportedProtocolVersion(value.Trim()))
        {
            return true;
        }

        var supported = string.Join(", ", McpJsonRpcDispatcher.SupportedVersionsHeader
            .Split(", ", StringSplitOptions.RemoveEmptyEntries)
            .Select(version => $"\"{version}\""));
        error = string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{{\"code\":-32600,\"message\":\"UnsupportedProtocolVersionError\",\"data\":{{\"supported\":[{supported}]}}}}}}");
        return false;
    }

    /// <summary>Unauthorized requests never consume a rate-limit slot.</summary>
    public bool TryAcquireRateLimit(out int retryAfterSeconds) =>
        _limiter.TryAcquire(_clock.UtcNow, out retryAfterSeconds);

    private static bool IsLoopbackAuthority(string authority, int port)
    {
        if (string.IsNullOrWhiteSpace(authority))
        {
            return false;
        }

        var candidate = authority.Trim();
        return string.Equals(candidate, $"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(candidate, $"localhost:{port}", StringComparison.OrdinalIgnoreCase);
    }
}
