using AiAssistant.Core.ValueObjects;
using AiAssistant.Platform.Linux.PipeWire;
using AiAssistant.Platform.Linux.Portal;
using Microsoft.Extensions.Logging.Abstractions;
using AiAssistant.Core.Configuration;

namespace AiAssistant.Platform.Tests;

public sealed class PipeWireTests
{
    [Fact]
    public void FormatRequest_RoundTripsThroughParser()
    {
        var pod = SpaPodCodec.BuildVideoFormatRequest(
            [SpaPodCodec.VideoFormatBgra, SpaPodCodec.VideoFormatRgba],
            maxWidth: 2560,
            maxHeight: 1440);

        Assert.True(pod.Length % 8 == 0);
        Assert.True(SpaPodCodec.TryParseVideoFormat(pod, out var format, out _, out _));
        Assert.Equal(SpaPodCodec.VideoFormatBgra, format);
    }

    [Fact]
    public void Converter_ConvertsBgraRowsAndRejectsShortBuffer()
    {
        byte[] source =
        [
            1, 2, 3, 255, 4, 5, 6, 255,
            7, 8, 9, 255, 10, 11, 12, 255
        ];

        Assert.True(PipeWireFrameConverter.TryConvertToBgra32(
            source,
            width: 2,
            height: 2,
            stride: 8,
            videoFormat: SpaPodCodec.VideoFormatBgra,
            out var bgra));

        Assert.NotNull(bgra);
        Assert.Equal(source, bgra);

        Assert.False(PipeWireFrameConverter.TryConvertToBgra32(
            source.AsSpan(0, 12),
            width: 2,
            height: 2,
            stride: 8,
            videoFormat: SpaPodCodec.VideoFormatBgra,
            out _));
    }

    [Fact]
    public void Converter_SwapsRgbaToBgra()
    {
        byte[] source = [10, 20, 30, 255, 40, 50, 60, 255];

        Assert.True(PipeWireFrameConverter.TryConvertToBgra32(
            source,
            width: 2,
            height: 1,
            stride: 8,
            videoFormat: SpaPodCodec.VideoFormatRgba,
            out var bgra));

        Assert.NotNull(bgra);
        Assert.Equal([30, 20, 10, 255, 60, 50, 40, 255], bgra);
    }

    [Fact]
    public void Converter_HandlesBottomUpRows()
    {
        // Bottom-up buffer: first row in memory is the last screen row.
        byte[] source =
        [
            7, 8, 9, 255, 10, 11, 12, 255,
            1, 2, 3, 255, 4, 5, 6, 255
        ];

        Assert.True(PipeWireFrameConverter.TryConvertToBgra32(
            source,
            width: 2,
            height: 2,
            stride: -8,
            videoFormat: SpaPodCodec.VideoFormatBgra,
            out var bgra));

        Assert.NotNull(bgra);
        Assert.Equal([1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255], bgra);
    }

    [Fact]
    public void Converter_RejectsYuvAndUnknownLayouts()
    {
        var source = new byte[16];

        Assert.False(PipeWireFrameConverter.TryConvertToBgra32(
            source,
            width: 2,
            height: 2,
            stride: 8,
            videoFormat: 2, // I420
            out _));
    }

    [Fact]
    public void Stream_IsDegradedBeforeStart()
    {
        using var stream = new PipeWireScreenCastStream(
            NullLogger<PipeWireScreenCastStream>.Instance,
            TimeSpan.FromSeconds(1));

        Assert.NotEqual(CapabilityStatus.Supported, stream.Capability.Status);
        Assert.Contains("idle", stream.Capability.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Real desktop check. Requires a Wayland session with xdg-desktop-portal and a user who can
    /// approve the screen-share dialog: AIASSISTANT_PORTAL_E2E=1 dotnet test --filter LiveWaylandScreenCast
    /// </summary>
    [Fact]
    public async Task LiveWaylandScreenCast_ProducesFrame()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnvironmentNames.PortalE2E),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        await using var client = new TmdsXdgPortalClient(TimeSpan.FromSeconds(120));
        await using var manager = new XdgPortalSessionManager(
            client,
            new NullRestoreTokenStore(),
            NullLogger<XdgPortalSessionManager>.Instance);

        var session = await manager.EnsureSessionAsync(CancellationToken.None);
        Assert.True(
            session is not null,
            $"portal session failed: {manager.Snapshot.Status} / {manager.Snapshot.Summary} / {manager.Snapshot.LastErrorType}");
        var descriptor = session!.Streams[0];

        using var remote = await client.OpenPipeWireRemoteAsync(session.SessionPath, CancellationToken.None);
        using var stream = new PipeWireScreenCastStream(
            NullLogger<PipeWireScreenCastStream>.Instance,
            TimeSpan.FromMilliseconds(200));

        var started = stream.TryStart(
            remote,
            descriptor.PipeWireSerial is > 0 ? (uint)descriptor.PipeWireSerial.Value : null,
            descriptor.NodeId,
            Math.Max(descriptor.Width, 1),
            Math.Max(descriptor.Height, 1),
            out var error);
        Assert.True(started, error);

        var frame = await stream.CaptureAsync(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        Assert.True(frame is not null, $"no frame received; capability={stream.Capability.Summary}");
        Assert.True(frame!.Width > 0);
        Assert.True(frame.Height > 0);
        Assert.Equal(frame.Width * frame.Height * 4, frame.Bgra32.Length);
    }

    private sealed class NullRestoreTokenStore : IPortalRestoreTokenStore
    {
        public Task<string?> LoadAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task SaveAsync(string token, CancellationToken ct) => Task.CompletedTask;

        public Task ClearAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
