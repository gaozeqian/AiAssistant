using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace AiAssistant.LocalApi;

/// <summary>Minimal HTTP/1.1 parsing and response writing for the loopback MCP endpoint.</summary>
internal static class McpHttpTransport
{
    public static bool PrefersEventStream(Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Accept", out var accept) || string.IsNullOrWhiteSpace(accept))
        {
            return false;
        }

        var wantsSse = accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
        var acceptsJson = accept.Contains("application/json", StringComparison.OrdinalIgnoreCase) ||
                          accept.Contains("*/*", StringComparison.Ordinal);
        return wantsSse && !acceptsJson;
    }

    public static async Task<McpHttpReadOutcome> ReadRequestAsync(
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
                return new McpHttpReadOutcome(null, 400, "Bad Request");
            }

            buffer.Write(chunk, 0, read);
            if (buffer.Length > 32 * 1024)
            {
                return new McpHttpReadOutcome(null, 431, "Request Header Fields Too Large");
            }

            headerEnd = FindHeaderEnd(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        }

        var headerText = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2)
        {
            return new McpHttpReadOutcome(null, 400, "Bad Request");
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
                return new McpHttpReadOutcome(null, 400, "Bad Request");
            }
        }

        if (headers.ContainsKey("Transfer-Encoding"))
        {
            return new McpHttpReadOutcome(null, 411, "Length Required");
        }

        if (bodyLength > maxBodyBytes)
        {
            return new McpHttpReadOutcome(null, 413, "Payload Too Large");
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
                return new McpHttpReadOutcome(null, 400, "Bad Request");
            }

            copied += read;
        }

        return new McpHttpReadOutcome(
            new McpHttpRequest(requestLine[0], requestLine[1], headers, Encoding.UTF8.GetString(body)),
            200,
            string.Empty);
    }

    public static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reason,
        CancellationToken ct,
        string? extraHeaders = null,
        string? json = null,
        string? contentType = null,
        string? sessionId = null)
    {
        var body = json is null ? [] : Encoding.UTF8.GetBytes(json);
        var builder = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {statusCode} {reason}\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Type: {contentType ?? "application/json; charset=utf-8"}\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n");
        if (!string.IsNullOrEmpty(sessionId))
        {
            builder.Append(CultureInfo.InvariantCulture, $"Mcp-Session-Id: {sessionId}\r\n");
        }

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
}

internal sealed record McpHttpRequest(
    string Method,
    string Path,
    Dictionary<string, string> Headers,
    string Body);

internal readonly record struct McpHttpReadOutcome(McpHttpRequest? Request, int StatusCode, string Error);
