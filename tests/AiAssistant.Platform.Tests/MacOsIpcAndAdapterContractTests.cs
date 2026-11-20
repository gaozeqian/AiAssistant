using System.Net.Sockets;
using System.Runtime.InteropServices;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Platform.MacOS;
using AiAssistant.Platform.MacOS.Ipc;
using AiAssistant.Storage.KeyStores;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiAssistant.Platform.Tests;

public sealed class MacOsIpcAndAdapterContractTests : IDisposable
{
    private readonly string _tempRoot;

    public MacOsIpcAndAdapterContractTests()
    {
        // AF_UNIX paths must stay short on every OS; artifacts is already the repo-local test scratch area.
        _tempRoot = Path.Combine(FindRepoRoot(), "artifacts", "test-temp", "s3", Guid.NewGuid().ToString("N")[..6]);
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
    public async Task RealUnixSocketHandshake_AuthenticatesOneTimeToken_AndFeedsMacOsAdapters()
    {
        var behavior = new FakeHelperBehavior();
        var launcher = new FakeMacHelperLauncher(behavior);
        await using var supervisor = CreateSupervisor(launcher, out var options);
        await supervisor.StartAsync(CancellationToken.None);

        Assert.Equal(MacHelperSupervisorStatus.Healthy, supervisor.Status);
        Assert.Equal(MacHelperProtocol.CurrentVersion, behavior.LastHandshakeProtocolVersion);
        Assert.False(string.IsNullOrWhiteSpace(behavior.LastReceivedToken));
        Assert.Equal(MacPermissionGrant.Granted, supervisor.Permissions.ScreenRecording);
        Assert.Equal(1, launcher.LaunchCount);

        var clock = new SystemClock();
        var windowSource = new MacOsWindowActivitySource(supervisor, clock);
        var screenCapture = new MacOsScreenCapture(supervisor, clock);
        var textExtractor = new MacOsTextExtractor(supervisor);
        var fileSource = new MacOsFileActivitySource(supervisor, clock, TimeSpan.FromMilliseconds(5));

        var window = await windowSource.GetCurrentForegroundAsync(CancellationToken.None);
        Assert.NotNull(window);
        Assert.Equal("Xcode", window!.AppName);
        Assert.Equal("AiAssistant", window.WindowTitle);
        Assert.Equal((nint)42, window.NativeWindowHandle);

        var screen = await screenCapture.CaptureFrameAsync(CancellationToken.None);
        Assert.NotNull(screen);
        Assert.Equal(1280, screen!.Width);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], screen.EncodedPngBytes);

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
        var audioCapture = new MacOsAudioCapture(supervisor, clock, TimeSpan.FromMilliseconds(5));
        using var audioCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var audioFrame = await audioCapture.ReadFramesAsync(audioCts.Token).FirstAsync(audioCts.Token);
        Assert.Equal(16_000, audioFrame.SampleRateHz);
        Assert.Equal(1, audioFrame.Channels);
        Assert.Equal([0.25f, -0.5f], audioFrame.Samples.ToArray());

        var capabilityProvider = new MacOsCapabilityProvider(
            screenCapture,
            windowSource,
            fileSource,
            textExtractor,
            audioCapture,
            new StaticKeyStore(new byte[32]),
            new MacOsLaunchAgentAutoStartService("/Applications/AiAssistant.app/Contents/MacOS/AiAssistant.Host", launchAgentsDirectory: _tempRoot),
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

        var screen = new MacOsScreenCapture(incompatibleSupervisor, new SystemClock());
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

        var screen = new MacOsScreenCapture(supervisor, new SystemClock());
        var window = new MacOsWindowActivitySource(supervisor, new SystemClock());
        var audio = new MacOsAudioCapture(supervisor, new SystemClock());
        Assert.Equal(CapabilityStatus.PermissionRequired, screen.Capability.Status);
        Assert.Equal(CapabilityStatus.Degraded, window.Capability.Status);
        Assert.Equal(CapabilityStatus.PermissionRequired, audio.Capability.Status);
        Assert.Null(await screen.CaptureFrameAsync(CancellationToken.None));

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
    public async Task RequestTimeoutIsBounded_AndUnexpectedDisconnectIsDiagnosable()
    {
        var behavior = new FakeHelperBehavior { PingDelayMs = 250 };
        var launcher = new FakeMacHelperLauncher(behavior);
        await using var supervisor = CreateSupervisor(launcher, out _, heartbeatMs: 2_000, requestTimeoutMs: 40);
        await supervisor.StartAsync(CancellationToken.None);

        var timedOut = await supervisor.InvokeAsync(
            new MacIpcRequest("slow-ping", MacHelperProtocol.Methods.Ping),
            CancellationToken.None);
        Assert.False(timedOut.Ok);
        Assert.Equal(MacHelperProtocol.ErrorCodes.Timeout, timedOut.ErrorCode);

        behavior.PingDelayMs = 0;
        behavior.DisconnectOnPing = true;
        await Task.Delay(300); // Let the deliberately slow first ping finish before testing a separate disconnect.
        var disconnected = await supervisor.InvokeAsync(
            new MacIpcRequest("disconnect", MacHelperProtocol.Methods.Ping),
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
        var service = new MacOsLaunchAgentAutoStartService(
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
                new(request.RequestId, true, ExtractedText: new MacExtractedTextDto("SAFE_AX_TEXT", "accessibility", 0.95)),
            MacHelperProtocol.Methods.PollFileEvents =>
                new(request.RequestId, true, FileChanges: behavior.NextFilePath is null ? [] : [new MacFileChangeDto(behavior.NextFilePath, "Modified")]),
            MacHelperProtocol.Methods.PollAudioFrames => BuildAudioResponse(request, behavior),
            _ => new(request.RequestId, false, ErrorCode: MacHelperProtocol.ErrorCodes.InternalError)
        };

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
