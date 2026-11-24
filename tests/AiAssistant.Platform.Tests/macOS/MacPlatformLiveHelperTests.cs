using System.Diagnostics;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Configuration;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Platform.macOS;
using AiAssistant.Platform.macOS.Ipc;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiAssistant.Platform.Tests;

/// <summary>
/// Real Swift Helper checks for macOS TTS feedback and TCC degradation (S7-R2 / S3-R1).
/// Skipped by default; requires a macOS session and a built helper:
///   bash build/native/mac-helper.sh build
///   AIASSISTANT_MAC_HELPER_E2E=1 dotnet test tests/AiAssistant.Platform.Tests \
///     --filter "FullyQualifiedName~MacPlatformLiveHelperTests"
/// The playback payload is digital silence so the check is inaudible.
/// </summary>
[Trait("Category", "LiveHardware")]
[Trait("OS", "macOS")]
public sealed class MacPlatformLiveHelperTests : IDisposable
{
    private readonly string _tempRoot;

    public MacPlatformLiveHelperTests()
    {
        _tempRoot = Path.Combine(ArtifactPaths.Resolve(FindRepoRoot(), "tmp", "s7"), Guid.NewGuid().ToString("N")[..6]);
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
            // Best-effort socket cleanup.
        }
    }

    [Fact]
    public async Task LiveHelperPlaysSilenceWithoutStarvingHeartbeat()
    {
        Assert.SkipUnless(
            LiveHelperEnabled,
            "Set AIASSISTANT_MAC_HELPER_E2E=1 and build the helper with build/native/mac-helper.sh build.");

        await using var supervisor = await StartSupervisorAsync();

        var playback = new AudioPlayback(supervisor);
        Assert.Equal(CapabilityStatus.Supported, playback.Capability.Status);

        // 3 s of silence at 24 kHz: exercises format setup, engine start and buffer scheduling.
        var silence = new float[24_000 * 3];
        var sw = Stopwatch.StartNew();
        await playback.PlayAsync(silence, 24_000, CancellationToken.None);
        sw.Stop();
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(1),
            $"audio.play must return immediately, took {sw.Elapsed.TotalMilliseconds:F0} ms");

        // Heartbeats and other IPC must stay responsive while the helper renders asynchronously.
        // Regression guard: TCC lookups used to run synchronously inside every ping (~1 s each).
        for (var i = 0; i < 3; i++)
        {
            var pingWatch = Stopwatch.StartNew();
            var ping = await supervisor.InvokeAsync(
                new MacIpcRequest($"live-ping-{i}", MacHelperProtocol.Methods.Ping),
                CancellationToken.None);
            pingWatch.Stop();
            Assert.True(ping.Ok);
            Assert.True(
                pingWatch.Elapsed < TimeSpan.FromMilliseconds(300),
                $"IPC ping blocked for {pingWatch.Elapsed.TotalMilliseconds:F0} ms during playback");
            await Task.Delay(100);
        }

        // A newer utterance supersedes the running one instead of overlapping.
        await playback.PlayAsync(new float[24_000], 24_000, CancellationToken.None);
        await playback.PlayAsync(ReadOnlyMemory<float>.Empty, 24_000, CancellationToken.None); // empty payload is a no-op

        await Task.Delay(300);
        Assert.Equal(MacHelperSupervisorStatus.Healthy, supervisor.Status);
        Assert.Equal(CapabilityStatus.Supported, playback.Capability.Status);
    }

    /// <summary>
    /// Proves the denial path on a real machine: missing TCC grants degrade only the affected
    /// capabilities, capture requests return null instead of throwing, and TTS feedback stays available.
    /// </summary>
    [Fact]
    public async Task LiveHelperDegradesPerTccStateWithoutCrashing()
    {
        Assert.SkipUnless(
            LiveHelperEnabled,
            "Set AIASSISTANT_MAC_HELPER_E2E=1 and build the helper with build/native/mac-helper.sh build.");

        await using var supervisor = await StartSupervisorAsync();
        var permissions = supervisor.Permissions;
        var screen = new ScreenCapture(supervisor, new SystemClock());
        var window = new WindowActivitySource(supervisor, new SystemClock());
        var audio = new AudioCapture(supervisor, new SystemClock());
        var playback = new AudioPlayback(supervisor);

        Assert.Equal(ExpectedScreenStatus(permissions.ScreenRecording), screen.Capability.Status);
        Assert.Equal(ExpectedWindowStatus(permissions.Accessibility), window.Capability.Status);
        Assert.Equal(ExpectedAudioStatus(permissions.Microphone), audio.Capability.Status);
        Assert.Equal(CapabilityStatus.Supported, playback.Capability.Status);

        if (permissions.ScreenRecording != MacPermissionGrant.Granted)
        {
            // Denied capture must degrade to "no frame", never to an exception or a restart.
            Assert.Empty(await screen.CaptureDisplaysAsync(new ScreenCaptureTarget(), CancellationToken.None));
            Assert.Equal(MacHelperSupervisorStatus.Healthy, supervisor.Status);
        }

        // Audio output has no TCC gate, so voice feedback remains usable while capture is denied.
        await playback.PlayAsync(new float[2_400], 24_000, CancellationToken.None);
        Assert.Equal(MacHelperSupervisorStatus.Healthy, supervisor.Status);
    }

    private static bool LiveHelperEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnvironmentNames.MacHelperE2E), "1", StringComparison.Ordinal);

    private static CapabilityStatus ExpectedScreenStatus(MacPermissionGrant grant) =>
        grant == MacPermissionGrant.Granted ? CapabilityStatus.Supported : CapabilityStatus.PermissionRequired;

    private static CapabilityStatus ExpectedWindowStatus(MacPermissionGrant grant) =>
        grant == MacPermissionGrant.Granted ? CapabilityStatus.Supported : CapabilityStatus.Degraded;

    private static CapabilityStatus ExpectedAudioStatus(MacPermissionGrant grant) =>
        grant switch
        {
            MacPermissionGrant.Granted => CapabilityStatus.Supported,
            MacPermissionGrant.Denied => CapabilityStatus.PermissionRequired,
            _ => CapabilityStatus.PermissionRequired
        };

    /// <summary>
    /// Crash supervision on a real helper: a killed helper must be relaunched with a fresh session,
    /// converge back to Healthy, and keep TTS playback working (S3-R1).
    /// </summary>
    [Fact]
    public async Task LiveHelperRecoversFromCrashWithFreshSession()
    {
        Assert.SkipUnless(
            LiveHelperEnabled,
            "Set AIASSISTANT_MAC_HELPER_E2E=1 and build the helper with build/native/mac-helper.sh build.");

        var helperPath = ResolveHelperPath();
        Assert.True(File.Exists(helperPath), $"Swift helper not built: {helperPath}");
        var launcher = new RecordingLauncher(new SubprocessMacHelperLauncher(helperPath));

        await using var supervisor = await StartSupervisorAsync(launcher);
        var playback = new AudioPlayback(supervisor);
        await playback.PlayAsync(new float[2_400], 24_000, CancellationToken.None);

        Assert.Single(launcher.Instances);
        var firstPid = launcher.Instances[0].ProcessId;
        Assert.NotNull(firstPid);

        using (var victim = System.Diagnostics.Process.GetProcessById(firstPid!.Value))
        {
            victim.Kill();
            victim.WaitForExit(5_000);
        }

        await WaitUntilAsync(
            () => supervisor.RestartCount >= 1 && supervisor.Status == MacHelperSupervisorStatus.Healthy,
            TimeSpan.FromSeconds(20));

        Assert.Equal(2, launcher.Instances.Count);
        Assert.NotEqual(firstPid, launcher.Instances[1].ProcessId);
        Assert.Equal(CapabilityStatus.Supported, playback.Capability.Status);
        await playback.PlayAsync(new float[2_400], 24_000, CancellationToken.None);
        Assert.Equal(MacHelperSupervisorStatus.Healthy, supervisor.Status);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(predicate(), "Expected supervisor state was not reached before timeout.");
    }

    private sealed class RecordingLauncher(IMacHelperProcessLauncher inner) : IMacHelperProcessLauncher
    {
        public List<IMacHelperProcessInstance> Instances { get; } = [];

        public async Task<IMacHelperProcessInstance> LaunchAsync(
            string socketPath,
            string oneTimeAuthToken,
            CancellationToken ct)
        {
            var instance = await inner.LaunchAsync(socketPath, oneTimeAuthToken, ct);
            Instances.Add(instance);
            return instance;
        }
    }

    private async Task<MacHelperSupervisor> StartSupervisorAsync(IMacHelperProcessLauncher? launcher = null)
    {
        var helperPath = ResolveHelperPath();
        Assert.True(File.Exists(helperPath), $"Swift helper not built: {helperPath}");

        var supervisor = new MacHelperSupervisor(
            launcher ?? new SubprocessMacHelperLauncher(helperPath),
            new MacHelperOptions
            {
                HelperExecutablePath = helperPath,
                SocketDirectory = _tempRoot,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                RequestTimeout = TimeSpan.FromSeconds(3),
                HeartbeatInterval = TimeSpan.FromMilliseconds(300),
                InitialBackoff = TimeSpan.FromMilliseconds(100),
                MaxBackoff = TimeSpan.FromMilliseconds(400)
            },
            NullLogger<MacHelperSupervisor>.Instance);

        await supervisor.StartAsync(CancellationToken.None);
        Assert.Equal(MacHelperSupervisorStatus.Healthy, supervisor.Status);
        return supervisor;
    }

    private static string ResolveHelperPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(EnvironmentNames.MacHelperPath);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        return ArtifactPaths.Resolve(
            FindRepoRoot(),
            "obj",
            "native",
            "mac-helper",
            "release",
            "aiassistant-mac-helper");
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
}
