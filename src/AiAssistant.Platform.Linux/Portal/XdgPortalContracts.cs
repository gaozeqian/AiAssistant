using Microsoft.Win32.SafeHandles;

namespace AiAssistant.Platform.Linux.Portal;

public enum XdgPortalSessionStatus
{
    NotRequested = 0,
    AwaitingUser = 1,
    Ready = 2,
    PermissionRequired = 3,
    Degraded = 4,
    Unavailable = 5
}

public sealed record PortalStreamDescriptor(
    uint NodeId,
    ulong? PipeWireSerial,
    int Width,
    int Height,
    string? StreamId);

public sealed record PortalStartResult(
    IReadOnlyList<PortalStreamDescriptor> Streams,
    string? NextRestoreToken);

public sealed record PortalScreenCastSession(
    string SessionPath,
    IReadOnlyList<PortalStreamDescriptor> Streams,
    bool RestoreTokenUsed);

public sealed record XdgPortalSessionSnapshot(
    XdgPortalSessionStatus Status,
    string Summary,
    int ActiveStreamCount,
    long TokenRotationCount,
    string? LastErrorType)
{
    public static readonly XdgPortalSessionSnapshot Initial = new(
        XdgPortalSessionStatus.NotRequested,
        "Portal authorization is requested only when screen capture is used.",
        0,
        0,
        null);
}

public sealed class XdgPortalRequestException(uint responseCode, string operation) : Exception($"Portal {operation} ended with response code {responseCode}.")
{
    public uint ResponseCode { get; } = responseCode;
    public string Operation { get; } = operation;
}

/// <summary>
/// High-level portal operations; the production implementation uses Tmds.DBus.Protocol and Request::Response signals.
/// </summary>
public interface IXdgPortalClient : IAsyncDisposable
{
    Task<string> CreateSessionAsync(CancellationToken ct);
    Task SelectSourcesAsync(string sessionPath, string? restoreToken, CancellationToken ct);
    Task<PortalStartResult> StartAsync(string sessionPath, CancellationToken ct);
    Task<SafeFileHandle> OpenPipeWireRemoteAsync(string sessionPath, CancellationToken ct);
    Task<byte[]> CaptureScreenshotAsync(CancellationToken ct);
    Task CloseSessionAsync(string sessionPath, CancellationToken ct);
}

public interface IPortalRestoreTokenStore
{
    Task<string?> LoadAsync(CancellationToken ct);
    Task SaveAsync(string token, CancellationToken ct);
    Task ClearAsync(CancellationToken ct);
}
