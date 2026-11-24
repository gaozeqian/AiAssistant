using System.Net.Sockets;
using System.Runtime.InteropServices;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Configuration;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Platform.macOS;
using AiAssistant.Platform.macOS.Ipc;
using AiAssistant.Storage.KeyStores;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiAssistant.Platform.Tests;

[Trait("Category", "Integration")]
[Trait("OS", "macOS")]
public sealed class MacPlatformIpcAndAdapterContractTests : IDisposable
{
    private readonly string _tempRoot;

    public MacPlatformIpcAndAdapterContractTests()
    {
        // AF_UNIX paths must stay short on every OS; artifacts is already the repo-local test scratch area.
        _tempRoot = Path.Combine(ArtifactPaths.Resolve(FindRepoRoot(), "tmp", "s3"), Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(_tempRoot);
    }

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
            // Best-effort socket and test directory cleanup.
        }
    }

    [Fact]
    public async Task RealUnixSocketHandshake_AuthenticatesOneTimeToken_AndFeedsMacAdapters()
    {
        var behavior = new FakeHelperBehavior();
        var launcher = new FakeMacHelperLauncher(behavior);
        await using var supervisor = CreateSupervisor(launcher, out var options);
        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => supervisor.Status == MacHelperSupervisorStatus.Healthy);

        Assert.Equal(MacHelperSupervisorStatus.Healthy, supervisor.Status);
        Assert.Equal(MacHelperProtocol.CurrentVersion, behavior.LastHandshakeProtocolVersion);
        Assert.False(string.IsNullOrWhiteSpace(behavior.LastReceivedToken));
        Assert.Equal(MacPermissionGrant.Granted, supervisor.Permissions.ScreenRecording);
        Assert.Equal(1, launcher.LaunchCount);

        var clock = new SystemClock();
        var windowSource = new WindowActivitySource(supervisor, clock);
        var screenCapture = new ScreenCapture(supervisor, clock);
        var textExtractor = new TextExtractor(supervisor);
        var fileSource = new FileActivitySource(supervisor, clock, TimeSpan.FromMilliseconds(5));

        var window = await windowSource.GetCurrentForegroundAsync(CancellationToken.None);
        Assert.NotNull(window);
        Assert.Equal("Xcode", window!.AppName);
        Assert.Equal("AiAssistant", window.WindowTitle);
        Assert.Equal((nint)42, window.NativeWindowHandle);

        var screens = await screenCapture.CaptureDisplaysAsync(new ScreenCaptureTarget(), CancellationToken.None);
        var screen = Assert.Single(screens);
        Assert.Equal(1280, screen.Width);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], screen.EncodedPngBytes);
        Assert.True(screen.IsForegroundDisplay);

        var extracted = await textExtractor.ExtractAsync(
            new TextExtractionRequest(42, "Xcode", "AiAssistant"),
            CancellationToken.None);
        Assert.Equal("SAFE_AX_TEXT", extracted.Text);
        Assert.Equal(TextExtractionMethod.AccessibilityTree, extracted.Method);

        var whitelist = Path.Combine(_tempRoot, "whitelist");
        Directory.CreateDirectory(whitelist);
        var changedFile = Path.Combine(whitelist, "note.md");
        await File.WriteAllTextAsync(changedFile, "authorized synthetic note");
        behavior.NextFilePath = changedFile;

        using var fileCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var fileSample = await fileSource.CaptureAsync(
                new CaptureContext([whitelist], TimeSpan.FromSeconds(10)),
                fileCts.Token)
            .FirstAsync(fileCts.Token);
        Assert.Equal(SourceKind.FileActivity, fileSample.Source);
        Assert.Equal("authorized synthetic note", fileSample.ExtractedText);

        behavior.AudioSamples = [0.25f, -0.5f];
        var audioCapture = new AudioCapture(supervisor, clock, TimeSpan.FromMilliseconds(5));
        using var audioCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var audioFrame = await audioCapture.ReadFramesAsync(audioCts.Token).FirstAsync(audioCts.Token);
        Assert.Equal(16_000, audioFrame.SampleRateHz);
        Assert.Equal(1, audioFrame.Channels);
        Assert.Equal([0.25f, -0.5f], audioFrame.Samples.ToArray());

        var capabilityProvider = new CapabilityProvider(
            screenCapture,
            windowSource,
            fileSource,
            textExtractor,
            audioCapture,
            new StaticKeyStore(new byte[32]),
            new LaunchAgentAutoStartService("/Applications/AiAssistant.app/Contents/MacOS/AiAssistant.Host", launchAgentsDirectory: _tempRoot),
            supervisor);
        var capabilities = capabilityProvider.GetCapabilities();
        Assert.Contains(capabilities, c => c.Kind == CapabilityKind.ScreenCapture && c.Status == CapabilityStatus.Supported);
        Assert.Contains(capabilities, c => c.Kind == CapabilityKind.Ocr && c.Status == CapabilityStatus.Supported);

        // The socket is a genuine AF_UNIX transport, not a mocked response stream.
        Assert.True(File.Exists(launcher.Instances[0].SocketPath) || launcher.Instances[0].HasExited);
        Assert.Equal(options.SocketDirectory, Path.GetDirectoryName(launcher.Instances[0].SocketPath));
    }

    [Fact]
    public async Task HandshakeRejectsWrongToken_AndProtocolMismatchIsTerminalWithoutRestartLoop()
    {
        var badTokenBehavior = new FakeHelperBehavior { RejectEveryToken = true };
        var badTokenLauncher = new FakeMacHelperLauncher(badTokenBehavior);
        _ = await badTokenLauncher.LaunchAsync(
            Path.Combine(_tempRoot, "bad-auth.sock"),
            "expected-token",
            CancellationToken.None);
        var badTokenProcess = badTokenLauncher.Instances[0];
        await using (badTokenProcess)
        await using (var client = await UnixDomainSocketMacHelperClient.ConnectAsync(
                         badTokenProcess.SocketPath,
                         TimeSpan.FromSeconds(2),
                         CancellationToken.None))
        {
            var response = await client.SendAsync(
                new MacIpcRequest("bad-auth", MacHelperProtocol.Methods.Handshake, MacHelperProtocol.CurrentVersion, "wrong-token"),
                TimeSpan.FromSeconds(2),
                CancellationToken.None);
            Assert.False(response.Ok);
            Assert.Equal(MacHelperProtocol.ErrorCodes.Unauthorized, response.ErrorCode);
        }

        var incompatibleLauncher = new FakeMacHelperLauncher(new FakeHelperBehavior { PeerProtocolVersion = 999 });
        await using var incompatibleSupervisor = CreateSupervisor(incompatibleLauncher, out _);
        await incompatibleSupervisor.StartAsync(CancellationToken.None);
        Assert.Equal(MacHelperSupervisorStatus.ProtocolIncompatible, incompatibleSupervisor.Status);
        Assert.Equal(1, incompatibleLauncher.LaunchCount);

        await Task.Delay(120);
        Assert.Equal(1, incompatibleLauncher.LaunchCount); // No restart storm after a version mismatch.
        var unavailable = await incompatibleSupervisor.InvokeAsync(
            new MacIpcRequest("blocked", MacHelperProtocol.Methods.Ping),
            CancellationToken.None);
        Assert.Equal(MacHelperProtocol.ErrorCodes.Disconnected, unavailable.ErrorCode);

        var screen = new ScreenCapture(incompatibleSupervisor, new SystemClock());
        Assert.Equal(CapabilityStatus.Degraded, screen.Capability.Status);
        Assert.Contains("protocol", screen.Capability.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TccRevocationDegradesOnlyAffectedCapabilities_AndCrashRestartsWithFreshToken()
    {
        var behavior = new FakeHelperBehavior();
        var launcher = new FakeMacHelperLauncher(behavior);
        await using var supervisor = CreateSupervisor(launcher, out _, heartbeatMs: 25, initialBackoffMs: 20, maxBackoffMs: 60);
        await supervisor.StartAsync(CancellationToken.None);
        Assert.Equal(MacHelperSupervisorStatus.Healthy, supervisor.Status);

        behavior.ScreenRecording = MacPermissionGrant.Denied;
        behavior.Accessibility = MacPermissionGrant.Denied;
        behavior.Microphone = MacPermissionGrant.Denied;
        var permissionRefresh = await supervisor.InvokeAsync(
            new MacIpcRequest("refresh", MacHelperProtocol.Methods.QueryPermissions),
            CancellationToken.None);
        Assert.True(permissionRefresh.Ok);

        var screen = new ScreenCapture(supervisor, new SystemClock());
        var window = new WindowActivitySource(supervisor, new SystemClock());
        var audio = new AudioCapture(supervisor, new SystemClock());
        Assert.Equal(CapabilityStatus.PermissionRequired, screen.Capability.Status);
        Assert.Equal(CapabilityStatus.Degraded, window.Capability.Status);
        Assert.Equal(CapabilityStatus.PermissionRequired, audio.Capability.Status);
        Assert.Empty(await screen.CaptureDisplaysAsync(new ScreenCaptureTarget(), CancellationToken.None));

        var firstToken = behavior.LastReceivedToken;
        await launcher.Instances[0].CrashAsync();
        await WaitUntilAsync(() => supervisor.RestartCount >= 1 && supervisor.Status == MacHelperSupervisorStatus.Healthy);

        Assert.Equal(2, launcher.LaunchCount);
        Assert.Equal(1, supervisor.RestartCount);
        Assert.NotEqual(firstToken, behavior.LastReceivedToken);
        var recovered = await window.GetCurrentForegroundAsync(CancellationToken.None);
        Assert.Equal("Xcode-Restart2", recovered?.AppName);
    }

    [Fact]
    public async Task TtsPlaybackSendsFloat32PcmToHelper_AndCapabilityFollowsHelperHealth()
    {
        var behavior = new FakeHelperBehavior();
        var launcher = new FakeMacHelperLauncher(behavior);
        await using var supervisor = CreateSupervisor(launcher, out _);
        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => supervisor.Status == MacHelperSupervisorStatus.Healthy);

        var playback = new AudioPlayback(supervisor);
        Assert.Equal(CapabilityStatus.Supported, playback.Capability.Status);

        await playback.PlayAsync(new float[] { 0.25f, -0.5f, 0.75f }, 24_000, CancellationToken.None);
        Assert.Equal(1, behavior.PlayAudioCalls);
        Assert.Equal(24_000, behavior.LastPlayedSampleRateHz);
        Assert.Equal([0.25f, -0.5f, 0.75f], behavior.LastPlayedSamples!);

        // Pathological payloads are clamped to 30 seconds instead of blocking the session.
        var oversized = new float[24_000 * 40];
        await playback.PlayAsync(oversized, 24_000, CancellationToken.None);
        Assert.Equal(24_000 * 30, behavior.LastPlayedSamples!.Length);

        // Empty audio and invalid rates never hit the helper.
        await playback.PlayAsync(ReadOnlyMemory<float>.Empty, 16_000, CancellationToken.None);
        await playback.PlayAsync(new float[] { 0.1f }, 0, CancellationToken.None);
        Assert.Equal(2, behavior.PlayAudioCalls);
    }

    [Fact]
    public async Task TtsPlaybackDegradesWithoutBreakingVoiceCommands_WhenHelperIsUnavailable()
    {
        var supervisor = new MacHelperSupervisor(
            launcher: null,
            new MacHelperOptions { SocketDirectory = _tempRoot },
            NullLogger<MacHelperSupervisor>.Instance);
        await using var disposable = supervisor;
        await supervisor.StartAsync(CancellationToken.None);

        var playback = new AudioPlayback(supervisor);
        Assert.Equal(CapabilityStatus.Degraded, playback.Capability.Status);

        // Playback must be a no-throw degradation; the voice session keeps text feedback.
        await playback.PlayAsync(new float[] { 0.1f, 0.2f }, 16_000, CancellationToken.None);
    }

    [Fact]
    public async Task ImageOcrSendsPngThroughIpcAndReturnsVisionText()
    {
        var behavior = new FakeHelperBehavior();
        var launcher = new FakeMacHelperLauncher(behavior);
        await using var supervisor = CreateSupervisor(launcher, out _);
        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => supervisor.Status == MacHelperSupervisorStatus.Healthy);

        var ocr = new ImageOcrRecognizer(supervisor);
        Assert.Equal(CapabilityStatus.Supported, ocr.Capability.Status);

        var pixels = new byte[2 * 2 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x20;       // B
            pixels[i + 1] = 0x90;   // G
            pixels[i + 2] = 0xE0;   // R
            pixels[i + 3] = 0xFF;   // A
        }

        var text = await ocr.RecognizeAsync(new ImageFrameBuffer(pixels, 2, 2), CancellationToken.None);
        Assert.Equal("SAFE_AX_TEXT", text);
        Assert.False(string.IsNullOrWhiteSpace(behavior.LastTextPngBase64));

        // The adapter must send a real PNG, not raw pixels: check the signature bytes.
        var png = Convert.FromBase64String(behavior.LastTextPngBase64!);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png.Take(4));

        // Invalid buffers are rejected before any IPC traffic.
        var callsBefore = behavior.ExtractTextCalls;
        Assert.Null(await ocr.RecognizeAsync(new ImageFrameBuffer(new byte[4], 4, 4), CancellationToken.None));
        Assert.Equal(callsBefore, behavior.ExtractTextCalls);
    }

    [Fact]
    public async Task ImageOcrDegradesWhenHelperIsUnavailable()
    {
        var supervisor = new MacHelperSupervisor(
            launcher: null,
            new MacHelperOptions { SocketDirectory = _tempRoot },
            NullLogger<MacHelperSupervisor>.Instance);
        await using var disposable = supervisor;
        await supervisor.StartAsync(CancellationToken.None);

        var ocr = new ImageOcrRecognizer(supervisor);
        Assert.Equal(CapabilityStatus.Degraded, ocr.Capability.Status);
        Assert.Null(await ocr.RecognizeAsync(
            new ImageFrameBuffer(new byte[2 * 2 * 4], 2, 2),
            CancellationToken.None));
    }

    [Fact]
    public async Task RequestTimeoutIsBounded_AndUnexpectedDisconnectIsDiagnosable()
    {
        var behavior = new FakeHelperBehavior { PingDelayMs = 250 };
        var launcher = new FakeMacHelperLauncher(behavior);
        await using var process = (FakeMacHelperProcess)await launcher.LaunchAsync(
            Path.Combine(_tempRoot, "timeout.sock"),
            "timeout-test-token",
            CancellationToken.None);
        await using var client = await UnixDomainSocketMacHelperClient.ConnectAsync(
            process.SocketPath,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        var handshake = await client.SendAsync(
            new MacIpcRequest("timeout-handshake", MacHelperProtocol.Methods.Handshake, MacHelperProtocol.CurrentVersion, "timeout-test-token"),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        Assert.True(handshake.Ok);

        var timedOut = await client.SendAsync(
            new MacIpcRequest("slow-ping", MacHelperProtocol.Methods.Ping),
            TimeSpan.FromMilliseconds(40),
            CancellationToken.None);
        Assert.False(timedOut.Ok);
        Assert.Equal(MacHelperProtocol.ErrorCodes.Timeout, timedOut.ErrorCode);

        behavior.PingDelayMs = 0;
        behavior.DisconnectOnPing = true;
        await Task.Delay(300); // Let the deliberately slow first ping finish before testing a separate disconnect.
        var disconnected = await client.SendAsync(
            new MacIpcRequest("disconnect", MacHelperProtocol.Methods.Ping),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        Assert.False(disconnected.Ok);
        Assert.Equal(MacHelperProtocol.ErrorCodes.Disconnected, disconnected.ErrorCode);
    }

    [Fact]
    public async Task FrameCodecRoundTripsCamelCaseDtos_AndRejectsMalformedLengths()
    {
        await using var stream = new MemoryStream();
        var request = new MacIpcRequest("r1", MacHelperProtocol.Methods.ScreenCapture, ProtocolVersion: 1, MaxWidth: 1280);
        await MacHelperFrameCodec.WriteFrameAsync(stream, request, CancellationToken.None);
        stream.Position = 0;
        var decoded = await MacHelperFrameCodec.ReadFrameAsync<MacIpcRequest>(stream, CancellationToken.None);
        Assert.NotNull(decoded);
        Assert.Equal("r1", decoded!.RequestId);
        Assert.Equal(1280, decoded.MaxWidth);

        await using var oversized = new MemoryStream([0x02, 0x00, 0x00, 0x01]);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await MacHelperFrameCodec.ReadFrameAsync<MacIpcRequest>(oversized, CancellationToken.None));

        await using var truncated = new MemoryStream([0, 0, 0, 8, 1, 2]);
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await MacHelperFrameCodec.ReadFrameAsync<MacIpcRequest>(truncated, CancellationToken.None));
    }

    [Fact]
    public async Task LaunchAgentAutoStart_WritesUserPlistAndCanDisableWithoutElevation()
    {
        var directory = Path.Combine(_tempRoot, "LaunchAgents");
        var service = new LaunchAgentAutoStartService(
            "/Applications/AiAssistant.app/Contents/MacOS/AiAssistant.Host",
            "com.aiassistant.tests",
            directory);

        Assert.False(await service.IsEnabledAsync(CancellationToken.None));
        await service.SetEnabledAsync(true, CancellationToken.None);
        Assert.True(await service.IsEnabledAsync(CancellationToken.None));
        Assert.True(File.Exists(service.PlistFilePath));
        var plist = await File.ReadAllTextAsync(service.PlistFilePath!);
        Assert.Contains("RunAtLoad", plist, StringComparison.Ordinal);
        Assert.Contains("/Applications/AiAssistant.app/Contents/MacOS/AiAssistant.Host", plist, StringComparison.Ordinal);

        await service.SetEnabledAsync(false, CancellationToken.None);
        Assert.False(await service.IsEnabledAsync(CancellationToken.None));
        Assert.False(File.Exists(service.PlistFilePath));
    }

    private MacHelperSupervisor CreateSupervisor(
        FakeMacHelperLauncher launcher,
        out MacHelperOptions options,
        int heartbeatMs = 1_000,
        int requestTimeoutMs = 500,
        int initialBackoffMs = 20,
        int maxBackoffMs = 100)
    {
        options = new MacHelperOptions
        {
            SocketDirectory = _tempRoot,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            RequestTimeout = TimeSpan.FromMilliseconds(requestTimeoutMs),
            HeartbeatInterval = TimeSpan.FromMilliseconds(heartbeatMs),
            InitialBackoff = TimeSpan.FromMilliseconds(initialBackoffMs),
            MaxBackoff = TimeSpan.FromMilliseconds(maxBackoffMs)
        };
        return new MacHelperSupervisor(launcher, options, NullLogger<MacHelperSupervisor>.Instance);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 4_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(predicate(), "Expected supervisor state was not reached before timeout.");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AiAssistant.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class FakeHelperBehavior
    {
        public int PeerProtocolVersion { get; init; } = MacHelperProtocol.CurrentVersion;
        public bool RejectEveryToken { get; init; }
        public bool DisconnectOnPing { get; set; }
        public int PingDelayMs { get; set; }
        public MacPermissionGrant ScreenRecording { get; set; } = MacPermissionGrant.Granted;
        public MacPermissionGrant Accessibility { get; set; } = MacPermissionGrant.Granted;
        public MacPermissionGrant Microphone { get; set; } = MacPermissionGrant.Granted;
        public string? NextFilePath { get; set; }
        public float[]? AudioSamples { get; set; }
        public int PlayAudioCalls { get; set; }
        public int? LastPlayedSampleRateHz { get; set; }
        public float[]? LastPlayedSamples { get; set; }
        public int ExtractTextCalls { get; set; }
        public string? LastTextPngBase64 { get; set; }
        public string? LastReceivedToken { get; set; }
        public int? LastHandshakeProtocolVersion { get; set; }
        public int Generation { get; set; }

        public MacPermissionsDto Permissions() => new(
            GrantText(ScreenRecording),
            GrantText(Accessibility),
            GrantText(Microphone));

        private static string GrantText(MacPermissionGrant grant) => grant switch
        {
            MacPermissionGrant.Granted => "granted",
            MacPermissionGrant.Denied => "denied",
            MacPermissionGrant.NotDetermined => "not_determined",
            _ => "unknown"
        };
    }

    private sealed class FakeMacHelperLauncher(FakeHelperBehavior behavior) : IMacHelperProcessLauncher
    {
        public List<FakeMacHelperProcess> Instances { get; } = [];
        public int LaunchCount => Instances.Count;

        public async Task<IMacHelperProcessInstance> LaunchAsync(string socketPath, string oneTimeAuthToken, CancellationToken ct)
        {
            behavior.Generation = Instances.Count + 1;
            var process = new FakeMacHelperProcess(socketPath, oneTimeAuthToken, behavior);
            Instances.Add(process);
            await process.StartAsync(ct);
            return process;
        }
    }

    private sealed class FakeMacHelperProcess(string socketPath, string expectedToken, FakeHelperBehavior behavior) : IMacHelperProcessInstance
    {
        private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly CancellationTokenSource _cts = new();
        private Task? _serverTask;
        private volatile bool _hasExited;

        public string SocketPath { get; } = socketPath;
        public bool HasExited => _hasExited;
        public int? ProcessId => null;

        public Task StartAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(SocketPath))
            {
                File.Delete(SocketPath);
            }

            _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            _listener.Listen(1);
            _serverTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
            return Task.CompletedTask;
        }

        public Task CrashAsync() => StopAsync();

        public async ValueTask DisposeAsync() => await StopAsync();

        private async Task StopAsync()
        {
            if (_hasExited)
            {
                return;
            }

            _hasExited = true;
            await _cts.CancelAsync();
            try { _listener.Dispose(); } catch { }
            if (_serverTask is not null)
            {
                try { await _serverTask; } catch { }
            }

            _cts.Dispose();
        }

        private async Task RunAsync(CancellationToken ct)
        {
            Socket? connection = null;
            try
            {
                connection = await _listener.AcceptAsync(ct);
                await using var stream = new NetworkStream(connection, ownsSocket: true);
                var handshake = await MacHelperFrameCodec.ReadFrameAsync<MacIpcRequest>(stream, ct);
                if (handshake is null)
                {
                    return;
                }

                behavior.LastHandshakeProtocolVersion = handshake.ProtocolVersion;
                behavior.LastReceivedToken = handshake.AuthToken;
                if (behavior.RejectEveryToken || !string.Equals(handshake.AuthToken, expectedToken, StringComparison.Ordinal))
                {
                    await MacHelperFrameCodec.WriteFrameAsync(stream,
                        new MacIpcResponse(handshake.RequestId, false, ErrorCode: MacHelperProtocol.ErrorCodes.Unauthorized), ct);
                    return;
                }

                if (handshake.ProtocolVersion != behavior.PeerProtocolVersion)
                {
                    await MacHelperFrameCodec.WriteFrameAsync(stream,
                        new MacIpcResponse(handshake.RequestId, false, behavior.PeerProtocolVersion, MacHelperProtocol.ErrorCodes.UnsupportedVersion), ct);
                    return;
                }

                await MacHelperFrameCodec.WriteFrameAsync(stream,
                    new MacIpcResponse(handshake.RequestId, true, behavior.PeerProtocolVersion, Permissions: behavior.Permissions()), ct);

                while (!ct.IsCancellationRequested)
                {
                    var request = await MacHelperFrameCodec.ReadFrameAsync<MacIpcRequest>(stream, ct);
                    if (request is null)
                    {
                        return;
                    }

                    if (request.Method == MacHelperProtocol.Methods.Shutdown)
                    {
                        await MacHelperFrameCodec.WriteFrameAsync(stream, new MacIpcResponse(request.RequestId, true), ct);
                        return;
                    }

                    if (request.Method == MacHelperProtocol.Methods.Ping)
                    {
                        if (behavior.DisconnectOnPing)
                        {
                            return;
                        }

                        if (behavior.PingDelayMs > 0)
                        {
                            await Task.Delay(behavior.PingDelayMs, ct);
                        }
                    }

                    var response = BuildResponse(request);
                    await MacHelperFrameCodec.WriteFrameAsync(stream, response, ct);
                }
            }
            catch
            {
                // A crash or cancellation intentionally terminates this fake helper.
            }
            finally
            {
                _hasExited = true;
                connection?.Dispose();
            }
        }

        private MacIpcResponse BuildResponse(MacIpcRequest request) => request.Method switch
        {
            MacHelperProtocol.Methods.Ping or MacHelperProtocol.Methods.QueryPermissions =>
                new(request.RequestId, true, MacHelperProtocol.CurrentVersion, Permissions: behavior.Permissions()),
            MacHelperProtocol.Methods.ForegroundWindow =>
                new(request.RequestId, true, Window: new MacForegroundWindowDto($"Xcode{(behavior.Generation > 1 ? $"-Restart{behavior.Generation}" : string.Empty)}", "AiAssistant", "com.apple.dt.Xcode", 42)),
            MacHelperProtocol.Methods.ScreenCapture when behavior.ScreenRecording != MacPermissionGrant.Granted =>
                new(request.RequestId, false, ErrorCode: MacHelperProtocol.ErrorCodes.PermissionDenied, Permissions: behavior.Permissions()),
            MacHelperProtocol.Methods.ScreenCapture =>
                new(request.RequestId, true, ScreenFrame: new MacScreenFrameDto(1280, 720, 123, Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]))),
            MacHelperProtocol.Methods.ExtractText =>
                BuildExtractTextResponse(request, behavior),
            MacHelperProtocol.Methods.PollFileEvents =>
                new(request.RequestId, true, FileChanges: behavior.NextFilePath is null ? [] : [new MacFileChangeDto(behavior.NextFilePath, "Modified")]),
            MacHelperProtocol.Methods.PollAudioFrames => BuildAudioResponse(request, behavior),
            MacHelperProtocol.Methods.PlayAudio => BuildPlayAudioResponse(request, behavior),
            _ => new(request.RequestId, false, ErrorCode: MacHelperProtocol.ErrorCodes.InternalError)
        };

        private static MacIpcResponse BuildExtractTextResponse(MacIpcRequest request, FakeHelperBehavior state)
        {
            state.ExtractTextCalls++;
            state.LastTextPngBase64 = request.PngBase64;
            return new MacIpcResponse(
                request.RequestId,
                true,
                ExtractedText: new MacExtractedTextDto("SAFE_AX_TEXT", request.PngBase64 is null ? "accessibility" : "vision", 0.95));
        }

        private static MacIpcResponse BuildPlayAudioResponse(MacIpcRequest request, FakeHelperBehavior state)
        {
            state.PlayAudioCalls++;
            state.LastPlayedSampleRateHz = request.SampleRateHz;

            if (request.Float32PcmBase64 is null)
            {
                state.LastPlayedSamples = null;
                return new MacIpcResponse(request.RequestId, false, ErrorCode: MacHelperProtocol.ErrorCodes.InternalError);
            }

            var bytes = Convert.FromBase64String(request.Float32PcmBase64);
            state.LastPlayedSamples = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
            return new MacIpcResponse(request.RequestId, true);
        }

        private static MacIpcResponse BuildAudioResponse(MacIpcRequest request, FakeHelperBehavior state)
        {
            if (state.AudioSamples is null)
            {
                return new MacIpcResponse(request.RequestId, true);
            }

            var bytes = MemoryMarshal.AsBytes(state.AudioSamples.AsSpan()).ToArray();
            return new MacIpcResponse(
                request.RequestId,
                true,
                AudioChunk: new MacAudioChunkDto(16_000, 1, Convert.ToBase64String(bytes)));
        }
    }
}
