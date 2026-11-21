using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using Tmds.DBus.Protocol;

namespace AiAssistant.Platform.Linux.Accessibility;

public sealed record AtSpiAccessibleObject(string BusName, string ObjectPath);

public sealed record AtSpiWindowSnapshot(string AppName, string Title, bool IsFocused, AtSpiAccessibleObject Reference)
{
    public nint StableHandle
    {
        get
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{Reference.BusName}\u001F{Reference.ObjectPath}"));
            return (nint)(BitConverter.ToInt64(hash) & long.MaxValue);
        }
    }
}

public interface IAtSpiAccessibilityClient : IAsyncDisposable
{
    CapabilityState Capability { get; }
    Task<AtSpiWindowSnapshot?> GetFocusedWindowAsync(CancellationToken ct);
    Task<string?> ExtractTextAsync(nint windowHandle, CancellationToken ct);
}

/// <summary>
/// AT-SPI2 client over the dedicated accessibility D-Bus. It discovers the bus through org.a11y.Bus,
/// enumerates accessible applications/windows and reads Text interfaces while excluding password roles.
/// </summary>
public sealed class TmdsAtSpiClient : IAtSpiAccessibilityClient
{
    private const string AccessibleInterface = "org.a11y.atspi.Accessible";
    private const string TextInterface = "org.a11y.atspi.Text";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const uint RoleDialog = 16;
    private const uint RoleFrame = 23;
    private const uint RoleLabel = 29;
    private const uint RolePasswordText = 40;
    private const uint RoleWindow = 69;
    private const uint StateActive = 1;
    private const uint StateFocused = 12;

    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly Dictionary<nint, AtSpiWindowSnapshot> _knownWindows = [];
    private DBusConnection? _a11yConnection;
    private string? _lastErrorType;
    private bool _hasConnected;
    private bool _disposed;

    public CapabilityState Capability => !OperatingSystem.IsLinux()
        ? CapabilityState.Unavailable(CapabilityKind.AccessibilityText, "AT-SPI2 is unavailable on this host")
        : _lastErrorType is not null
            ? CapabilityState.Degraded(CapabilityKind.AccessibilityText, $"AT-SPI2 degraded ({_lastErrorType})")
            : _hasConnected
                ? CapabilityState.Supported(CapabilityKind.AccessibilityText, "AT-SPI2 accessibility bus connected")
                : CapabilityState.Degraded(CapabilityKind.AccessibilityText, "AT-SPI2 will be probed on first use");

    public async Task<AtSpiWindowSnapshot?> GetFocusedWindowAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            await EnsureConnectedAsync(ct);
            var root = new AtSpiAccessibleObject("org.a11y.atspi.Registry", "/org/a11y/atspi/accessible/root");
            var applications = await GetChildrenAsync(root, ct);
            AtSpiWindowSnapshot? activeFallback = null;
            foreach (var app in applications.Take(64))
            {
                var appName = await GetNameAsync(app, ct) ?? "Unknown";
                var children = await GetChildrenAsync(app, ct);
                foreach (var child in children.Take(128))
                {
                    var role = await GetRoleAsync(child, ct);
                    if (role is not (RoleDialog or RoleFrame or RoleWindow))
                    {
                        continue;
                    }

                    var states = await GetStatesAsync(child, ct);
                    if (!states.Contains(StateActive) && !states.Contains(StateFocused))
                    {
                        continue;
                    }

                    var title = await GetNameAsync(child, ct);
                    var snapshot = new AtSpiWindowSnapshot(
                        appName,
                        string.IsNullOrWhiteSpace(title) ? appName : title,
                        states.Contains(StateFocused),
                        child);
                    Remember(snapshot);
                    if (snapshot.IsFocused)
                    {
                        return snapshot;
                    }

                    activeFallback ??= snapshot;
                }
            }

            _lastErrorType = null;
            return activeFallback;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MarkConnectionFailure(ex);
            return null;
        }
    }

    public async Task<string?> ExtractTextAsync(nint windowHandle, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            await EnsureConnectedAsync(ct);
            AtSpiWindowSnapshot? window;
            lock (_knownWindows)
            {
                _knownWindows.TryGetValue(windowHandle, out window);
            }

            if (window is null)
            {
                window = await GetFocusedWindowAsync(ct);
            }

            if (window is null || (windowHandle != 0 && window.StableHandle != windowHandle))
            {
                return null;
            }

            var collection = new TextCollectionState();
            await CollectTextAsync(window.Reference, 0, collection, ct);
            return collection.Fragments.Count == 0 ? null : string.Join(" | ", collection.Fragments);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MarkConnectionFailure(ex);
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _a11yConnection?.Dispose();
            _a11yConnection = null;
            _connectionGate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task CollectTextAsync(
        AtSpiAccessibleObject target,
        int depth,
        TextCollectionState state,
        CancellationToken ct)
    {
        if (depth > 5 || state.Visited >= 96 || state.TotalChars >= 2048)
        {
            return;
        }

        state.Visited++;
        var role = await GetRoleAsync(target, ct);
        if (role == RolePasswordText)
        {
            return; // Privacy boundary: never inspect password text controls.
        }

        var interfaces = await CallAsync(
            target,
            AccessibleInterface,
            "GetInterfaces",
            string.Empty,
            null,
            reader => reader.ReadArrayOfString(),
            ct);
        if (interfaces.Contains(TextInterface, StringComparer.Ordinal))
        {
            var countValue = await GetPropertyAsync(target, TextInterface, "CharacterCount", ct);
            if (countValue.Type == VariantValueType.Int32)
            {
                var count = Math.Clamp(countValue.GetInt32(), 0, 2048 - state.TotalChars);
                if (count > 0)
                {
                    var text = await CallAsync(
                        target,
                        TextInterface,
                        "GetText",
                        "ii",
                        writer =>
                        {
                            writer.WriteInt32(0);
                            writer.WriteInt32(count);
                        },
                        reader => reader.ReadString(),
                        ct);
                    AddFragment(text, state);
                }
            }
        }
        else if (role == RoleLabel)
        {
            AddFragment(await GetNameAsync(target, ct), state);
        }

        if (depth == 5 || state.TotalChars >= 2048)
        {
            return;
        }

        foreach (var child in (await GetChildrenAsync(target, ct)).Take(96))
        {
            await CollectTextAsync(child, depth + 1, state, ct);
            if (state.Visited >= 96 || state.TotalChars >= 2048)
            {
                break;
            }
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_a11yConnection is not null)
        {
            return;
        }

        await _connectionGate.WaitAsync(ct);
        try
        {
            if (_a11yConnection is not null)
            {
                return;
            }

            var sessionAddress = DBusAddress.Session
                ?? throw new InvalidOperationException("D-Bus session bus is unavailable.");
            using var session = new DBusConnection(sessionAddress);
            await session.ConnectAsync().AsTask().WaitAsync(ct);
            MessageBuffer request;
            using (var writer = session.GetMessageWriter())
            {
                writer.WriteMethodCallHeader("org.a11y.Bus", "/org/a11y/bus", "org.a11y.Bus", "GetAddress", string.Empty);
                request = writer.CreateMessage();
            }

            var address = await session.CallMethodAsync(
                request,
                static (Message message, object? _) => message.GetBodyReader().ReadString(),
                null).WaitAsync(ct);
            var connection = new DBusConnection(address);
            try
            {
                await connection.ConnectAsync().AsTask().WaitAsync(ct);
                _a11yConnection = connection;
                _hasConnected = true;
                _lastErrorType = null;
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

    private Task<List<AtSpiAccessibleObject>> GetChildrenAsync(AtSpiAccessibleObject target, CancellationToken ct) =>
        CallAsync(target, AccessibleInterface, "GetChildren", string.Empty, null, ReadChildren, ct);

    private Task<uint> GetRoleAsync(AtSpiAccessibleObject target, CancellationToken ct) =>
        CallAsync(target, AccessibleInterface, "GetRole", string.Empty, null, reader => reader.ReadUInt32(), ct);

    private Task<uint[]> GetStatesAsync(AtSpiAccessibleObject target, CancellationToken ct) =>
        CallAsync(target, AccessibleInterface, "GetState", string.Empty, null, reader => reader.ReadArrayOfUInt32(), ct);

    private async Task<string?> GetNameAsync(AtSpiAccessibleObject target, CancellationToken ct)
    {
        var value = await GetPropertyAsync(target, AccessibleInterface, "Name", ct);
        return value.Type == VariantValueType.String ? value.GetString() : null;
    }

    private Task<VariantValue> GetPropertyAsync(AtSpiAccessibleObject target, string interfaceName, string property, CancellationToken ct) =>
        CallAsync(
            target,
            PropertiesInterface,
            "Get",
            "ss",
            writer =>
            {
                writer.WriteString(interfaceName);
                writer.WriteString(property);
            },
            reader => reader.ReadVariantValue(),
            ct);

    private async Task<T> CallAsync<T>(
        AtSpiAccessibleObject target,
        string interfaceName,
        string member,
        string signature,
        Action<MessageWriter>? writeBody,
        Func<Reader, T> read,
        CancellationToken ct)
    {
        var connection = _a11yConnection ?? throw new InvalidOperationException("AT-SPI bus is not connected.");
        MessageBuffer request;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(target.BusName, target.ObjectPath, interfaceName, member, signature);
            writeBody?.Invoke(writer);
            request = writer.CreateMessage();
        }

        return await connection.CallMethodAsync(
            request,
            (Message message, object? _) => read(message.GetBodyReader()),
            null).WaitAsync(ct);
    }

    private static List<AtSpiAccessibleObject> ReadChildren(Reader reader)
    {
        var children = new List<AtSpiAccessibleObject>();
        var end = reader.ReadArrayStart(DBusType.Struct);
        while (reader.HasNext(end) && children.Count < 256)
        {
            reader.AlignStruct();
            var bus = reader.ReadString();
            var path = reader.ReadObjectPathAsString();
            if (!string.IsNullOrWhiteSpace(bus) && !string.IsNullOrWhiteSpace(path))
            {
                children.Add(new AtSpiAccessibleObject(bus, path));
            }
        }

        return children;
    }

    private static void AddFragment(string? text, TextCollectionState state)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length < 2 || state.Fragments.Contains(trimmed, StringComparer.Ordinal))
        {
            return;
        }

        var allowed = Math.Min(trimmed.Length, 2048 - state.TotalChars);
        if (allowed <= 0)
        {
            return;
        }

        state.Fragments.Add(trimmed[..allowed]);
        state.TotalChars += allowed;
    }

    private sealed class TextCollectionState
    {
        public List<string> Fragments { get; } = [];
        public int Visited { get; set; }
        public int TotalChars { get; set; }
    }

    private void Remember(AtSpiWindowSnapshot snapshot)
    {
        lock (_knownWindows)
        {
            if (_knownWindows.Count >= 32)
            {
                _knownWindows.Remove(_knownWindows.Keys.First());
            }

            _knownWindows[snapshot.StableHandle] = snapshot;
        }
    }

    private void MarkConnectionFailure(Exception ex)
    {
        _lastErrorType = ex.GetType().Name;
        _a11yConnection?.Dispose();
        _a11yConnection = null;
    }
}

public sealed class AtSpiWindowActivitySource(IAtSpiAccessibilityClient client, IClock clock) : IWindowActivitySource
{
    public SourceKind Kind => SourceKind.WindowActivity;

    public CapabilityState Capability => client.Capability.Status == CapabilityStatus.Unavailable
        ? CapabilityState.Degraded(CapabilityKind.WindowActivity, "Wayland compositor does not expose a global active window; AT-SPI unavailable")
        : CapabilityState.Degraded(
            CapabilityKind.WindowActivity,
            "Wayland active/focused window through AT-SPI2 (compositor/application dependent)",
            "Enable desktop accessibility support; global window enumeration is intentionally restricted on Wayland.");

    public async Task<CapturedSample?> GetCurrentForegroundAsync(CancellationToken ct)
    {
        var window = await client.GetFocusedWindowAsync(ct);
        if (window is null)
        {
            return null;
        }

        return new CapturedSample(
            clock.Now(),
            SourceKind.WindowActivity,
            window.AppName,
            window.Title,
            NativeWindowHandle: window.StableHandle,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal) { ["backend"] = "wayland-at-spi" });
    }

    public async IAsyncEnumerable<CapturedSample> CaptureAsync(CaptureContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        string? lastSignature = null;
        var lastEmit = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var current = await GetCurrentForegroundAsync(ct);
            if (current is null)
            {
                continue;
            }

            var signature = $"{current.AppName}\u001F{current.WindowTitle}";
            if (string.Equals(signature, lastSignature, StringComparison.Ordinal) && clock.UtcNow - lastEmit < context.FallbackInterval)
            {
                continue;
            }

            lastSignature = signature;
            lastEmit = clock.UtcNow;
            yield return current;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
