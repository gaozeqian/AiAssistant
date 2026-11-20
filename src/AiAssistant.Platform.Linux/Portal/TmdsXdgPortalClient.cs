using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace AiAssistant.Platform.Linux.Portal;

/// <summary>
/// Native xdg-desktop-portal client over the D-Bus session bus using Tmds.DBus.Protocol 0.95.
/// Request signal subscriptions are installed before method calls to avoid the portal response race.
/// </summary>
public sealed class TmdsXdgPortalClient : IXdgPortalClient
{
    private const string Destination = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string ScreenCastInterface = "org.freedesktop.portal.ScreenCast";
    private const string ScreenshotInterface = "org.freedesktop.portal.Screenshot";
    private const string RequestInterface = "org.freedesktop.portal.Request";

    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly TimeSpan _requestTimeout;
    private DBusConnection? _connection;
    private string? _senderPath;
    private bool _disposed;

    public TmdsXdgPortalClient(TimeSpan? requestTimeout = null)
    {
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(90);
    }

    public async Task<string> CreateSessionAsync(CancellationToken ct)
    {
        var response = await InvokeRequestAsync(
            ScreenCastInterface,
            "CreateSession",
            "a{sv}",
            (writer, token) =>
            {
                var options = WriteOptionsStart(writer, token);
                WriteStringOption(writer, "session_handle_token", $"aia{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}");
                writer.WriteDictionaryEnd(options);
                return null;
            },
            ct);
        return GetRequiredString(response.Results, "session_handle");
    }

    public Task SelectSourcesAsync(string sessionPath, string? restoreToken, CancellationToken ct) =>
        InvokeRequestAsync(
            ScreenCastInterface,
            "SelectSources",
            "oa{sv}",
            (writer, token) =>
            {
                writer.WriteObjectPath(sessionPath);
                var options = WriteOptionsStart(writer, token);
                WriteUInt32Option(writer, "types", 1); // MONITOR
                WriteBoolOption(writer, "multiple", false);
                WriteUInt32Option(writer, "cursor_mode", 2); // Embedded
                WriteUInt32Option(writer, "persist_mode", 2); // Until explicitly revoked
                if (!string.IsNullOrWhiteSpace(restoreToken))
                {
                    WriteStringOption(writer, "restore_token", restoreToken);
                }

                writer.WriteDictionaryEnd(options);
                return null;
            },
            ct);

    public async Task<PortalStartResult> StartAsync(string sessionPath, CancellationToken ct)
    {
        var response = await InvokeRequestAsync(
            ScreenCastInterface,
            "Start",
            "osa{sv}",
            (writer, token) =>
            {
                writer.WriteObjectPath(sessionPath);
                writer.WriteString(string.Empty); // parent_window
                var options = WriteOptionsStart(writer, token);
                writer.WriteDictionaryEnd(options);
                return null;
            },
            ct);

        var streams = ReadStreams(response.Results);
        var nextToken = response.Results.TryGetValue("restore_token", out var token) && token.Type == VariantValueType.String
            ? token.GetString()
            : null;
        return new PortalStartResult(streams, nextToken);
    }

    public async Task<SafeFileHandle> OpenPipeWireRemoteAsync(string sessionPath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        var connection = _connection!;
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Destination, PortalPath, ScreenCastInterface, "oa{sv}", "OpenPipeWireRemote");
            writer.WriteObjectPath(sessionPath);
            var options = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(options);
            message = writer.CreateMessage();
        }

        return await connection.CallMethodAsync(
            message,
            static (Message message, object? _) => message.GetBodyReader().ReadHandle<SafeFileHandle>(),
            null).WaitAsync(ct);
    }

    public async Task<byte[]> CaptureScreenshotAsync(CancellationToken ct)
    {
        var response = await InvokeRequestAsync(
            ScreenshotInterface,
            "Screenshot",
            "sa{sv}",
            (writer, token) =>
            {
                writer.WriteString(string.Empty);
                var options = WriteOptionsStart(writer, token);
                WriteBoolOption(writer, "modal", false);
                WriteBoolOption(writer, "interactive", false);
                WriteUInt32Option(writer, "target", 1); // Entire screen
                writer.WriteDictionaryEnd(options);
                return null;
            },
            ct);

        var uriText = GetRequiredString(response.Results, "uri");
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || !uri.IsFile)
        {
            throw new InvalidDataException("Portal screenshot did not return a local file URI.");
        }

        await using var stream = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > 32 * 1024 * 1024)
        {
            throw new InvalidDataException("Portal screenshot size exceeds the 32 MiB safety bound.");
        }

        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, ct);
        return bytes;
    }

    public async Task CloseSessionAsync(string sessionPath, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        var connection = _connection!;
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Destination, sessionPath, "org.freedesktop.portal.Session", string.Empty, "Close");
            message = writer.CreateMessage();
        }

        await connection.CallMethodAsync(message).WaitAsync(ct);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _connection?.Dispose();
            _connection = null;
            _connectionGate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<PortalResponse> InvokeRequestAsync(
        string interfaceName,
        string member,
        string signature,
        Func<MessageWriter, string, object?> writeBody,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureConnectedAsync(ct);
        var connection = _connection!;
        var token = $"aia{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}";
        var expectedPath = $"/org/freedesktop/portal/desktop/request/{_senderPath}/{token}";
        var completion = new TaskCompletionSource<PortalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptions = new List<IDisposable>(2);

        try
        {
            subscriptions.Add(await SubscribeToResponseAsync(connection, expectedPath, completion));
            MessageBuffer methodCall;
            using (var writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(Destination, PortalPath, interfaceName, signature, member);
                _ = writeBody(writer, token);
                methodCall = writer.CreateMessage();
            }

            var actualPath = await connection.CallMethodAsync(
                methodCall,
                static (Message message, object? _) => message.GetBodyReader().ReadObjectPathAsString(),
                null).WaitAsync(ct);

            if (!string.Equals(actualPath, expectedPath, StringComparison.Ordinal))
            {
                subscriptions.Add(await SubscribeToResponseAsync(connection, actualPath, completion));
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_requestTimeout);
            var response = await completion.Task.WaitAsync(timeout.Token);
            if (response.ResponseCode != 0)
            {
                throw new XdgPortalRequestException(response.ResponseCode, member);
            }

            return response;
        }
        finally
        {
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }
        }
    }

    private static async Task<IDisposable> SubscribeToResponseAsync(
        DBusConnection connection,
        string requestPath,
        TaskCompletionSource<PortalResponse> completion)
    {
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Sender = Destination,
            Interface = RequestInterface,
            Member = "Response",
            Path = requestPath
        };

        return await connection.AddMatchAsync(
            rule,
            static (Message message, object? _) => ReadPortalResponse(message),
            notification =>
            {
                if (notification.HasValue)
                {
                    completion.TrySetResult(notification.Value);
                }
                else if (notification.CompletionReason is not null)
                {
                    completion.TrySetException(notification.CompletionReason);
                }
            },
            emitOnCapturedContext: false,
            flags: ObserverFlags.None,
            state: null);
    }

    private static PortalResponse ReadPortalResponse(Message message)
    {
        var reader = message.GetBodyReader();
        var code = reader.ReadUInt32();
        var results = reader.ReadDictionaryOfStringToVariantValue();
        return new PortalResponse(code, results);
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_connection is not null)
        {
            return;
        }

        await _connectionGate.WaitAsync(ct);
        try
        {
            if (_connection is not null)
            {
                return;
            }

            var address = DBusAddress.Session
                ?? throw new InvalidOperationException("D-Bus session bus address is unavailable.");
            var connection = new DBusConnection(address);
            try
            {
                await connection.ConnectAsync().AsTask().WaitAsync(ct);
                var uniqueName = connection.UniqueName
                    ?? throw new InvalidOperationException("D-Bus session did not provide a unique name.");
                _senderPath = uniqueName.TrimStart(':').Replace('.', '_');
                _connection = connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private static ArrayStart WriteOptionsStart(MessageWriter writer, string handleToken)
    {
        var options = writer.WriteDictionaryStart();
        WriteStringOption(writer, "handle_token", handleToken);
        return options;
    }

    private static void WriteStringOption(MessageWriter writer, string key, string value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariant(VariantValue.String(value));
    }

    private static void WriteUInt32Option(MessageWriter writer, string key, uint value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariant(VariantValue.UInt32(value));
    }

    private static void WriteBoolOption(MessageWriter writer, string key, bool value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariant(VariantValue.Bool(value));
    }

    private static string GetRequiredString(IReadOnlyDictionary<string, VariantValue> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value.Type != VariantValueType.String)
        {
            throw new InvalidDataException($"Portal response is missing string result '{key}'.");
        }

        return value.GetString();
    }

    private static List<PortalStreamDescriptor> ReadStreams(IReadOnlyDictionary<string, VariantValue> values)
    {
        if (!values.TryGetValue("streams", out var streamsValue) || streamsValue.Type != VariantValueType.Array)
        {
            return [];
        }

        var streams = new List<PortalStreamDescriptor>(Math.Max(0, streamsValue.Count));
        for (var i = 0; i < streamsValue.Count; i++)
        {
            var stream = streamsValue.GetItem(i);
            if (stream.Type != VariantValueType.Struct || stream.Count < 2)
            {
                continue;
            }

            var nodeId = stream.GetItem(0).GetUInt32();
            var properties = stream.GetItem(1).GetDictionary<string, VariantValue>();
            var width = 0;
            var height = 0;
            if (properties.TryGetValue("size", out var size) && size.Type == VariantValueType.Struct && size.Count >= 2)
            {
                width = size.GetItem(0).GetInt32();
                height = size.GetItem(1).GetInt32();
            }

            ulong? serial = properties.TryGetValue("pipewire-serial", out var serialValue) && serialValue.Type == VariantValueType.UInt64
                ? serialValue.GetUInt64()
                : null;
            string? id = properties.TryGetValue("id", out var idValue) && idValue.Type == VariantValueType.String
                ? idValue.GetString()
                : null;
            streams.Add(new PortalStreamDescriptor(nodeId, serial, width, height, id));
        }

        return streams;
    }

    private sealed record PortalResponse(uint ResponseCode, IReadOnlyDictionary<string, VariantValue> Results);
}
