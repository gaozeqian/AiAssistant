using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace AiAssistant.Platform.Linux.Portal;

/// <summary>
/// Native xdg-desktop-portal client over the D-Bus session bus using Tmds.DBus.Protocol 0.95.
/// Request signal subscriptions are installed before method calls to avoid the portal response race.
/// Message bodies are written inline because <see cref="MessageWriter"/> is a mutable struct:
/// passing it to delegates or helpers by value silently drops the body and makes the bus drop the connection.
/// </summary>
public sealed class TmdsXdgPortalClient : IXdgPortalClient
{
    private const string Destination = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string ScreenCastInterface = "org.freedesktop.portal.ScreenCast";
    private const string ScreenshotInterface = "org.freedesktop.portal.Screenshot";
    private const string RequestInterface = "org.freedesktop.portal.Request";
    private const string SessionInterface = "org.freedesktop.portal.Session";

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
        var token = NewToken(8);
        MessageBuffer message;
        await using var lease = await BeginAsync("CreateSession", "a{sv}", token, ct);
        using (var writer = lease.Connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Destination, PortalPath, ScreenCastInterface, "CreateSession", "a{sv}");
            var options = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString("handle_token");
            writer.WriteVariant(VariantValue.String(token));
            writer.WriteDictionaryEntryStart();
            writer.WriteString("session_handle_token");
            writer.WriteVariant(VariantValue.String(NewToken(6)));
            writer.WriteDictionaryEnd(options);
            message = writer.CreateMessage();
        }

        var response = await lease.SendAsync(message, ct);
        return GetRequiredString(response.Results, "session_handle");
    }

    public async Task SelectSourcesAsync(string sessionPath, string? restoreToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);
        var token = NewToken(8);
        MessageBuffer message;
        await using var lease = await BeginAsync("SelectSources", "oa{sv}", token, ct);
        using (var writer = lease.Connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Destination, PortalPath, ScreenCastInterface, "SelectSources", "oa{sv}");
            writer.WriteObjectPath(sessionPath);
            var options = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString("handle_token");
            writer.WriteVariant(VariantValue.String(token));
            writer.WriteDictionaryEntryStart();
            writer.WriteString("types");
            writer.WriteVariant(VariantValue.UInt32(1)); // MONITOR
            writer.WriteDictionaryEntryStart();
            writer.WriteString("multiple");
            writer.WriteVariant(VariantValue.Bool(false));
            writer.WriteDictionaryEntryStart();
            writer.WriteString("cursor_mode");
            writer.WriteVariant(VariantValue.UInt32(2)); // Embedded
            writer.WriteDictionaryEntryStart();
            writer.WriteString("persist_mode");
            writer.WriteVariant(VariantValue.UInt32(2)); // Until explicitly revoked
            if (!string.IsNullOrWhiteSpace(restoreToken))
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("restore_token");
                writer.WriteVariant(VariantValue.String(restoreToken));
            }

            writer.WriteDictionaryEnd(options);
            message = writer.CreateMessage();
        }

        _ = await lease.SendAsync(message, ct);
    }

    public async Task<PortalStartResult> StartAsync(string sessionPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);
        var token = NewToken(8);
        MessageBuffer message;
        await using var lease = await BeginAsync("Start", "osa{sv}", token, ct);
        using (var writer = lease.Connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Destination, PortalPath, ScreenCastInterface, "Start", "osa{sv}");
            writer.WriteObjectPath(sessionPath);
            writer.WriteString(string.Empty); // parent_window
            var options = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString("handle_token");
            writer.WriteVariant(VariantValue.String(token));
            writer.WriteDictionaryEnd(options);
            message = writer.CreateMessage();
        }

        var response = await lease.SendAsync(message, ct);
        var streams = ReadStreams(response.Results);
        var nextToken = response.Results.TryGetValue("restore_token", out var tokenValue) &&
                        tokenValue.Type == VariantValueType.String
            ? tokenValue.GetString()
            : null;
        return new PortalStartResult(streams, nextToken);
    }

    public async Task<SafeFileHandle> OpenPipeWireRemoteAsync(string sessionPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureConnectedAsync(ct);
        var connection = _connection!;
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                Destination,
                PortalPath,
                ScreenCastInterface,
                "OpenPipeWireRemote",
                "oa{sv}");
            writer.WriteObjectPath(sessionPath);
            var options = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(options);
            message = writer.CreateMessage();
        }

        return await connection.CallMethodAsync(
            message,
            static (Message reply, object? _) => reply.GetBodyReader().ReadHandle<SafeFileHandle>(),
            null).WaitAsync(ct);
    }

    public async Task<byte[]> CaptureScreenshotAsync(CancellationToken ct)
    {
        var token = NewToken(8);
        MessageBuffer message;
        await using var lease = await BeginAsync("Screenshot", "sa{sv}", token, ct);
        using (var writer = lease.Connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Destination, PortalPath, ScreenshotInterface, "Screenshot", "sa{sv}");
            writer.WriteString(string.Empty);
            var options = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString("handle_token");
            writer.WriteVariant(VariantValue.String(token));
            writer.WriteDictionaryEntryStart();
            writer.WriteString("modal");
            writer.WriteVariant(VariantValue.Bool(false));
            writer.WriteDictionaryEntryStart();
            writer.WriteString("interactive");
            writer.WriteVariant(VariantValue.Bool(false));
            writer.WriteDictionaryEntryStart();
            writer.WriteString("target");
            writer.WriteVariant(VariantValue.UInt32(1)); // Entire screen
            writer.WriteDictionaryEnd(options);
            message = writer.CreateMessage();
        }

        var response = await lease.SendAsync(message, ct);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureConnectedAsync(ct);
        var connection = _connection!;
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Destination, sessionPath, SessionInterface, "Close", string.Empty);
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

    private static string NewToken(int byteCount) =>
        $"aia{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(byteCount))}";




    private async Task<RequestLease> BeginAsync(
        string member,
        string signature,
        string token,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureConnectedAsync(ct);
        var connection = _connection!;
        var expectedPath = $"/org/freedesktop/portal/desktop/request/{_senderPath}/{token}";
        var completion = new TaskCompletionSource<PortalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = await SubscribeToResponseAsync(connection, expectedPath, completion);
        return new RequestLease(this, connection, expectedPath, member, completion, subscription);
    }

    private async Task<PortalResponse> AwaitResponseAsync(
        RequestLease lease,
        MessageBuffer message,
        CancellationToken ct)
    {
        var actualPath = await lease.Connection.CallMethodAsync(
            message,
            static (Message reply, object? _) => reply.GetBodyReader().ReadObjectPathAsString(),
            null).WaitAsync(ct);

        if (!string.Equals(actualPath, lease.ExpectedPath, StringComparison.Ordinal))
        {
            var extra = await SubscribeToResponseAsync(lease.Connection, actualPath, lease.Completion);
            lease.AddSubscription(extra);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_requestTimeout);
        var response = await lease.Completion.Task.WaitAsync(timeout.Token);
        if (response.ResponseCode != 0)
        {
            throw new XdgPortalRequestException(response.ResponseCode, lease.Member);
        }

        return response;
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
            if (properties.TryGetValue("size", out var size) &&
                size.Type == VariantValueType.Struct &&
                size.Count >= 2)
            {
                width = size.GetItem(0).GetInt32();
                height = size.GetItem(1).GetInt32();
            }

            ulong? serial = properties.TryGetValue("pipewire-serial", out var serialValue) &&
                             serialValue.Type == VariantValueType.UInt64
                ? serialValue.GetUInt64()
                : null;
            string? id = properties.TryGetValue("id", out var idValue) && idValue.Type == VariantValueType.String
                ? idValue.GetString()
                : null;
            streams.Add(new PortalStreamDescriptor(nodeId, serial, width, height, id));
        }

        return streams;
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

    private sealed record PortalResponse(uint ResponseCode, IReadOnlyDictionary<string, VariantValue> Results);

    /// <summary>
    /// Holds the pending Response subscription. Exposed as a class so message bodies are written by
    /// callers against the real <see cref="MessageWriter"/> instead of a struct copy.
    /// </summary>
    private sealed class RequestLease : IAsyncDisposable
    {
        private readonly TmdsXdgPortalClient _owner;
        private readonly List<IDisposable> _subscriptions;

        public RequestLease(
            TmdsXdgPortalClient owner,
            DBusConnection connection,
            string expectedPath,
            string member,
            TaskCompletionSource<PortalResponse> completion,
            IDisposable subscription)
        {
            _owner = owner;
            Connection = connection;
            ExpectedPath = expectedPath;
            Member = member;
            Completion = completion;
            _subscriptions = [subscription];
        }

        public DBusConnection Connection { get; }

        public string ExpectedPath { get; }

        public string Member { get; }

        public TaskCompletionSource<PortalResponse> Completion { get; }

        public void AddSubscription(IDisposable subscription) => _subscriptions.Add(subscription);

        public Task<PortalResponse> SendAsync(MessageBuffer message, CancellationToken ct) =>
            _owner.AwaitResponseAsync(this, message, ct);

        public ValueTask DisposeAsync()
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
            return ValueTask.CompletedTask;
        }
    }
}
