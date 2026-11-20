using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Platform.Linux;
using AiAssistant.Platform.Linux.Accessibility;
using AiAssistant.Platform.Linux.Desktop;
using AiAssistant.Platform.Linux.Portal;
using AiAssistant.Platform.Linux.X11;
using AiAssistant.Storage.KeyStores;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace AiAssistant.Platform.Tests;

public sealed class LinuxPlatformContractTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(AppContext.BaseDirectory, "test-temp", Guid.NewGuid().ToString("N"));

    public LinuxPlatformContractTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }

    [Fact]
    public void BackendSelector_DefaultsToWayland_AndFallsBackToX11OnlyWhenAvailable()
    {
        var session = new LinuxDesktopSessionSnapshot(true, true, true, null, "GNOME", "wayland");
        var state = new LinuxDisplayBackendState(session);
        Assert.Equal(LinuxDisplayBackendKind.Wayland, state.Decision.Backend);
        Assert.Equal(LinuxBackendSelectionReason.DefaultWayland, state.Decision.Reason);
        Assert.True(state.Decision.CanFallbackToX11);

        Assert.True(state.TryMarkInitializationFallback("AvaloniaInitializationException"));
        Assert.Equal(LinuxDisplayBackendKind.X11, state.Decision.Backend);
        Assert.Equal(LinuxBackendSelectionReason.InitializationFailure, state.Decision.Reason);
        Assert.Contains("AvaloniaInitializationException", state.Decision.Summary, StringComparison.Ordinal);
        Assert.False(state.TryMarkInitializationFallback("SecondFailure"));

        var forced = LinuxDisplayBackendSelector.Select(session with { ForcedBackend = "X11" });
        Assert.Equal(LinuxDisplayBackendKind.X11, forced.Backend);
        Assert.Equal(LinuxBackendSelectionReason.ForcedByUser, forced.Reason);
        Assert.False(forced.CanFallbackToX11);

        var noDisplay = LinuxDisplayBackendSelector.Select(session with { HasWaylandDisplay = false, HasX11Display = false });
        Assert.Equal(LinuxDisplayBackendKind.Unavailable, noDisplay.Backend);
        Assert.False(noDisplay.CanFallbackToX11);
    }

    [Fact]
    public async Task PortalSession_RotatesSingleUseToken_AndRestoresItAfterHostRestart()
    {
        var store = new FakeTokenStore();
        var firstClient = new FakePortalClient();
        await using (var manager = CreateManager(firstClient, store))
        {
            var session = await manager.EnsureSessionAsync(CancellationToken.None);
            Assert.NotNull(session);
            Assert.False(session!.RestoreTokenUsed);
            Assert.Equal("token-1", store.Token);
            Assert.Equal(1, manager.Snapshot.TokenRotationCount);
            Assert.Same(session, await manager.EnsureSessionAsync(CancellationToken.None));
            Assert.Equal(1, firstClient.CreateCount);
            Assert.Equal(1, firstClient.OpenRemoteCount);
        }

        Assert.Equal(1, firstClient.CloseCount);
        Assert.Equal("token-1", store.Token); // Normal shutdown must preserve the next restore token.

        var secondClient = new FakePortalClient(initialSequence: 1);
        await using var restartedManager = CreateManager(secondClient, store);
        var restored = await restartedManager.EnsureSessionAsync(CancellationToken.None);
        Assert.NotNull(restored);
        Assert.True(restored!.RestoreTokenUsed);
        Assert.Equal(["token-1"], secondClient.RestoreTokensReceived);
        Assert.Equal("token-2", store.Token);
        Assert.Equal(1, restartedManager.Snapshot.TokenRotationCount);
    }

    [Fact]
    public async Task PortalRevokedToken_IsClearedAndRetriedExactlyOnceWithoutIt()
    {
        var store = new FakeTokenStore { Token = "expired-token" };
        var client = new FakePortalClient { RejectStartWithRestoreToken = true };
        await using var manager = CreateManager(client, store);

        var session = await manager.EnsureSessionAsync(CancellationToken.None);
        Assert.NotNull(session);
        Assert.Equal(["expired-token", null], client.RestoreTokensReceived);
        Assert.Equal(2, client.CreateCount);
        Assert.Equal("token-2", store.Token);
        Assert.Equal(XdgPortalSessionStatus.Ready, manager.Snapshot.Status);
        Assert.False(session!.RestoreTokenUsed);
    }

    [Fact]
    public async Task PortalUserCancellation_DoesNotRetryOrTriggerASecondDialog()
    {
        var store = new FakeTokenStore { Token = "still-unused-token" };
        var client = new FakePortalClient { CancelAllStarts = true };
        await using var manager = CreateManager(client, store);

        var session = await manager.EnsureSessionAsync(CancellationToken.None);
        Assert.Null(session);
        Assert.Equal(1, client.CreateCount);
        Assert.Equal(["still-unused-token"], client.RestoreTokensReceived);
        Assert.Equal("still-unused-token", store.Token);
        Assert.Equal(XdgPortalSessionStatus.PermissionRequired, manager.Snapshot.Status);
    }

    [Fact]
    public async Task WaylandCapture_DecodesPortalPng_AndPermissionRevocationOnlyDegradesCapture()
    {
        var store = new FakeTokenStore();
        var client = new FakePortalClient();
        await using var manager = CreateManager(client, store);
        var capture = new WaylandScreenCapture(client, manager, new SystemClock(), NullLogger<WaylandScreenCapture>.Instance);

        var frame = await capture.CaptureFrameAsync(CancellationToken.None);
        Assert.NotNull(frame);
        Assert.Equal(2, frame!.Width);
        Assert.Equal(2, frame.Height);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], frame.EncodedPngBytes[..4]);

        client.ScreenshotFailure = new XdgPortalRequestException(1, "Screenshot");
        var afterRevocation = await capture.CaptureFrameAsync(CancellationToken.None);
        Assert.Null(afterRevocation);
        Assert.Null(store.Token);
        Assert.Equal(XdgPortalSessionStatus.PermissionRequired, manager.Snapshot.Status);
        Assert.Equal(CapabilityStatus.Degraded, capture.Capability.Status);
    }

    [Fact]
    public async Task RestoreTokenStore_EncryptsAtRest_DetectsTampering_AndClearsOnlyItsOwnFile()
    {
        var directory = Path.Combine(_tempRoot, "portal");
        var store = new ProtectedPortalRestoreTokenStore(directory, new StaticKeyStore(new byte[32]));
        const string token = "single-use-portal-secret-token-123456789";
        await store.SaveAsync(token, CancellationToken.None);
        var path = Path.Combine(directory, "screencast-token.enc");
        var encrypted = await File.ReadAllBytesAsync(path);
        Assert.DoesNotContain(token, System.Text.Encoding.UTF8.GetString(encrypted), StringComparison.Ordinal);
        Assert.Equal(token, await store.LoadAsync(CancellationToken.None));

        encrypted[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, encrypted);
        Assert.Null(await store.LoadAsync(CancellationToken.None));
        await store.ClearAsync(CancellationToken.None);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void PngCodecAndX11PixelConverter_RoundTripColorsAndDimensions()
    {
        var bgra = new byte[]
        {
            255, 0, 0, 255,   0, 255, 0, 255,
            0, 0, 255, 255,   255, 255, 255, 255
        };
        var png = LinuxPngCodec.EncodeBgra32(bgra, 2, 2);
        Assert.True(LinuxPngCodec.TryDecodeToBgra32(png, out var decoded, out var width, out var height, out var hash));
        Assert.Equal(2, width);
        Assert.Equal(2, height);
        Assert.Equal(bgra, decoded);
        Assert.Equal(PerceptualHash.ComputeFromBgra32(decoded!, width, height, width * 4), hash);

        // X11 little-endian 32bpp pixels: B, G, R, unused.
        var x11Bgra = X11PixelConverter.DownsampleToBgra32(
            bgra,
            2,
            2,
            stride: 8,
            bitsPerPixel: 32,
            byteOrder: 0,
            redMask: 0x00FF0000,
            greenMask: 0x0000FF00,
            blueMask: 0x000000FF,
            targetWidth: 2,
            targetHeight: 2);
        Assert.Equal(bgra, x11Bgra);

        Assert.False(LinuxPngCodec.TryDecodeToBgra32([1, 2, 3], out _, out _, out _, out _));
    }

    [Fact]
    public async Task XdgAutoStart_WritesQuotedExecAtomically_AndCanDisable()
    {
        var directory = Path.Combine(_tempRoot, "autostart");
        var service = new XdgAutoStartService("/opt/Ai Assistant/AiAssistant.Host", directory);
        Assert.False(await service.IsEnabledAsync(CancellationToken.None));
        await service.SetEnabledAsync(true, CancellationToken.None);
        Assert.True(await service.IsEnabledAsync(CancellationToken.None));
        var content = await File.ReadAllTextAsync(service.DesktopFilePath!);
        Assert.Contains("Exec=\"/opt/Ai Assistant/AiAssistant.Host\"", content, StringComparison.Ordinal);
        Assert.Contains("X-GNOME-Autostart-enabled=true", content, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));

        await service.SetEnabledAsync(false, CancellationToken.None);
        Assert.False(await service.IsEnabledAsync(CancellationToken.None));
        Assert.False(File.Exists(service.DesktopFilePath));
    }

    [Fact]
    public async Task LinuxInotifySource_EnforcesFileLimits_AndStreamsAuthorizedTextChange()
    {
        var directory = Path.Combine(_tempRoot, "whitelist");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "note.md");
        await File.WriteAllTextAsync(file, "initial");
        Assert.Equal("initial", LinuxInotifyFileActivitySource.TryReadSafeTextSnippet(file, 2048));

        var oversized = Path.Combine(_tempRoot, "large.txt");
        await File.WriteAllBytesAsync(oversized, new byte[3000]);
        Assert.Null(LinuxInotifyFileActivitySource.TryReadSafeTextSnippet(oversized, 2048));
        var binary = Path.Combine(_tempRoot, "secret.pfx");
        await File.WriteAllBytesAsync(binary, [1, 2, 3]);
        Assert.Null(LinuxInotifyFileActivitySource.TryReadSafeTextSnippet(binary, 2048));

        var source = new LinuxInotifyFileActivitySource(new SystemClock());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = source.CaptureAsync(new CaptureContext([directory], TimeSpan.FromSeconds(10)), cts.Token)
            .GetAsyncEnumerator(cts.Token);
        var pending = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(120);
        await File.WriteAllTextAsync(file, "authorized changed text");
        var completed = await Task.WhenAny(pending, Task.Delay(3000, cts.Token));
        Assert.Same(pending, completed);
        Assert.True(await pending);
        Assert.Equal(SourceKind.FileActivity, enumerator.Current.Source);
        Assert.Equal("note.md", enumerator.Current.Metadata!["fileName"]);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task AtSpiWaylandSource_AndTextExtractor_ReuseAccessibilityContract()
    {
        var atSpi = new FakeAtSpiClient();
        var session = new LinuxDesktopSessionSnapshot(true, true, true, null, "KDE", "wayland");
        var state = new LinuxDisplayBackendState(session);
        var clock = new SystemClock();
        var source = new LinuxWindowActivitySource(state, atSpi, clock, new X11ScreenCapture(clock));

        var current = await source.GetCurrentForegroundAsync(CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal("Editor", current!.AppName);
        Assert.Equal("Wayland Window", current.WindowTitle);
        Assert.Equal(CapabilityStatus.Degraded, new AtSpiWindowActivitySource(atSpi, clock).Capability.Status);

        var extractor = new LinuxTextExtractor(atSpi);
        var extracted = await extractor.ExtractAsync(
            new TextExtractionRequest(current.NativeWindowHandle, current.AppName, current.WindowTitle),
            CancellationToken.None);
        Assert.Equal("SAFE_AT_SPI_TEXT", extracted.Text);
        Assert.Equal(TextExtractionMethod.AccessibilityTree, extracted.Method);
        Assert.Equal(CapabilityStatus.Degraded, extractor.OcrCapability.Status);
    }

    private static XdgPortalSessionManager CreateManager(IXdgPortalClient client, IPortalRestoreTokenStore store) =>
        new(client, store, NullLogger<XdgPortalSessionManager>.Instance);

    private sealed class FakeTokenStore : IPortalRestoreTokenStore
    {
        public string? Token { get; set; }
        public int ClearCount { get; private set; }
        public Task<string?> LoadAsync(CancellationToken ct) => Task.FromResult(Token);
        public Task SaveAsync(string token, CancellationToken ct)
        {
            Token = token;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct)
        {
            Token = null;
            ClearCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePortalClient(int initialSequence = 0) : IXdgPortalClient
    {
        private int _sequence = initialSequence;
        private static readonly byte[] Png = LinuxPngCodec.EncodeBgra32(
        [
            40, 80, 160, 255, 40, 80, 160, 255,
            40, 80, 160, 255, 40, 80, 160, 255
        ], 2, 2);

        public int CreateCount { get; private set; }
        public int CloseCount { get; private set; }
        public int OpenRemoteCount { get; private set; }
        public bool RejectStartWithRestoreToken { get; init; }
        public bool CancelAllStarts { get; init; }
        public Exception? ScreenshotFailure { get; set; }
        public List<string?> RestoreTokensReceived { get; } = [];
        public string? CurrentRestoreToken { get; private set; }

        public Task<string> CreateSessionAsync(CancellationToken ct)
        {
            CreateCount++;
            _sequence++;
            return Task.FromResult($"/session/{CreateCount}");
        }

        public Task SelectSourcesAsync(string sessionPath, string? restoreToken, CancellationToken ct)
        {
            RestoreTokensReceived.Add(restoreToken);
            CurrentRestoreToken = restoreToken;
            return Task.CompletedTask;
        }

        public Task<PortalStartResult> StartAsync(string sessionPath, CancellationToken ct)
        {
            if (CancelAllStarts)
            {
                throw new XdgPortalRequestException(1, "Start");
            }

            if (RejectStartWithRestoreToken && CurrentRestoreToken is not null)
            {
                throw new XdgPortalRequestException(2, "Start");
            }

            return Task.FromResult(new PortalStartResult(
                [new PortalStreamDescriptor(77, 1001, 1920, 1080, "monitor-1")],
                $"token-{_sequence}"));
        }

        public Task<SafeFileHandle> OpenPipeWireRemoteAsync(string sessionPath, CancellationToken ct)
        {
            OpenRemoteCount++;
            return Task.FromResult(new SafeFileHandle(-1, ownsHandle: true));
        }

        public Task<byte[]> CaptureScreenshotAsync(CancellationToken ct) =>
            ScreenshotFailure is null ? Task.FromResult(Png.ToArray()) : Task.FromException<byte[]>(ScreenshotFailure);

        public Task CloseSessionAsync(string sessionPath, CancellationToken ct)
        {
            CloseCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAtSpiClient : IAtSpiAccessibilityClient
    {
        private readonly AtSpiWindowSnapshot _window = new(
            "Editor",
            "Wayland Window",
            true,
            new AtSpiAccessibleObject(":1.42", "/org/a11y/atspi/accessible/1"));

        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.AccessibilityText, "Fake AT-SPI");
        public Task<AtSpiWindowSnapshot?> GetFocusedWindowAsync(CancellationToken ct) => Task.FromResult<AtSpiWindowSnapshot?>(_window);
        public Task<string?> ExtractTextAsync(nint windowHandle, CancellationToken ct) =>
            Task.FromResult<string?>(windowHandle == _window.StableHandle ? "SAFE_AT_SPI_TEXT" : null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
