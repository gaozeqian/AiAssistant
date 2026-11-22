using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiAssistant.Platform.macOS.Ipc;

/// <summary>
/// Versioned local IPC wire protocol (v1) between .NET host and macOS Swift helper (ADR-0005, IMPLEMENTATION.md S3).
/// Framing: 4-byte big-endian payload length (max 16 MiB) + UTF-8 JSON object.
/// </summary>
public static class MacHelperProtocol
{
    public const int CurrentVersion = 1;
    public const int MaxFrameBytes = 16 * 1024 * 1024;
    public const string AuthTokenEnvVar = "AIASSISTANT_IPC_TOKEN";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static class Methods
    {
        public const string Handshake = "handshake";
        public const string Ping = "ping";
        public const string QueryPermissions = "permissions.query";
        public const string ForegroundWindow = "window.foreground";
        public const string ScreenCapture = "screen.capture";
        public const string ExtractText = "text.extract";
        public const string PollFileEvents = "fs.poll";
        public const string PollAudioFrames = "audio.poll";
        public const string PlayAudio = "audio.play";
        public const string Shutdown = "shutdown";
    }

    public static class ErrorCodes
    {
        public const string Unauthorized = "unauthorized";
        public const string PermissionDenied = "permission_denied";
        public const string UnsupportedVersion = "unsupported_version";
        public const string Timeout = "timeout";
        public const string Disconnected = "disconnected";
        public const string InternalError = "internal_error";
    }
}

public enum MacPermissionGrant
{
    Unknown = 0,
    Granted = 1,
    Denied = 2,
    NotDetermined = 3
}

public sealed record MacTccPermissionSnapshot(
    MacPermissionGrant ScreenRecording,
    MacPermissionGrant Accessibility,
    MacPermissionGrant Microphone)
{
    public static readonly MacTccPermissionSnapshot AllUnknown = new(
        MacPermissionGrant.Unknown,
        MacPermissionGrant.Unknown,
        MacPermissionGrant.Unknown);

    public static MacPermissionGrant ParseGrant(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "granted" => MacPermissionGrant.Granted,
            "denied" => MacPermissionGrant.Denied,
            "not_determined" => MacPermissionGrant.NotDetermined,
            _ => MacPermissionGrant.Unknown
        };
}

public sealed record MacIpcRequest(
    string RequestId,
    string Method,
    int? ProtocolVersion = null,
    string? AuthToken = null,
    int? MaxWidth = null,
    long? WindowId = null,
    string? PngBase64 = null,
    IReadOnlyList<string>? Directories = null,
    int? SampleRateHz = null,
    string? Float32PcmBase64 = null);

public sealed record MacPermissionsDto(
    string? ScreenRecording,
    string? Accessibility,
    string? Microphone)
{
    public MacTccPermissionSnapshot ToDomainSnapshot() => new(
        MacTccPermissionSnapshot.ParseGrant(ScreenRecording),
        MacTccPermissionSnapshot.ParseGrant(Accessibility),
        MacTccPermissionSnapshot.ParseGrant(Microphone));
}

public sealed record MacForegroundWindowDto(
    string AppName,
    string WindowTitle,
    string? BundleId = null,
    long WindowId = 0);

public sealed record MacScreenFrameDto(
    int Width,
    int Height,
    ulong PerceptualHash,
    string PngBase64);

public sealed record MacExtractedTextDto(
    string Text,
    string Source,
    double Confidence);

public sealed record MacFileChangeDto(
    string FullPath,
    string Operation);

public sealed record MacAudioChunkDto(
    int SampleRateHz,
    int Channels,
    string Float32PcmBase64);

public sealed record MacIpcResponse(
    string RequestId,
    bool Ok,
    int? ProtocolVersion = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    MacPermissionsDto? Permissions = null,
    MacForegroundWindowDto? Window = null,
    MacScreenFrameDto? ScreenFrame = null,
    MacExtractedTextDto? ExtractedText = null,
    IReadOnlyList<MacFileChangeDto>? FileChanges = null,
    MacAudioChunkDto? AudioChunk = null);

public static class MacHelperFrameCodec
{
    public static async ValueTask WriteFrameAsync<T>(Stream stream, T payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, MacHelperProtocol.JsonOptions);
        if (jsonBytes.Length == 0 || jsonBytes.Length > MacHelperProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException($"IPC frame size {jsonBytes.Length} exceeds protocol bounds.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)jsonBytes.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(jsonBytes, ct);
        await stream.FlushAsync(ct);
    }

    public static async ValueTask<T?> ReadFrameAsync<T>(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        var headerRead = await ReadExactOrEofAsync(stream, header, ct);
        if (headerRead == 0)
        {
            return default;
        }

        if (headerRead < 4)
        {
            throw new EndOfStreamException("Truncated 4-byte IPC frame header.");
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0 || length > MacHelperProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException($"Peer sent invalid IPC frame length ({length} bytes).");
        }

        var buffer = new byte[length];
        var bodyRead = await ReadExactOrEofAsync(stream, buffer, ct);
        if (bodyRead < length)
        {
            throw new EndOfStreamException($"Truncated IPC frame payload (expected {length} bytes, got {bodyRead}).");
        }

        return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(buffer), MacHelperProtocol.JsonOptions);
    }

    private static async ValueTask<int> ReadExactOrEofAsync(Stream stream, Memory<byte> destination, CancellationToken ct)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = await stream.ReadAsync(destination[total..], ct);
            if (read == 0)
            {
                return total;
            }

            total += read;
        }

        return total;
    }
}
