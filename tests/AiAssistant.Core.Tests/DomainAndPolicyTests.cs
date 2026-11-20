using AiAssistant.Application.Policies;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Core.Tests;

public sealed class DomainAndPolicyTests
{
    private sealed class FakeClock(DateTimeOffset utcNow, TimeZoneInfo timeZone) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public TimeZoneInfo LocalTimeZone { get; set; } = timeZone;
    }

    [Fact]
    public void OccurredTimestamp_PreservesOriginalLocalDateAcrossTimezoneChanges()
    {
        // 2026-11-20 00:30:00 in UTC+8 is 2026-11-19 16:30:00 UTC.
        var shanghaiTz = TimeZoneInfo.CreateCustomTimeZone("Asia/Shanghai-Test", TimeSpan.FromHours(8), "CST", "CST");
        var newYorkTz = TimeZoneInfo.CreateCustomTimeZone("America/New_York-Test", TimeSpan.FromHours(-5), "EST", "EST");
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 19, 16, 30, 0, TimeSpan.Zero), shanghaiTz);

        var captured = ((IClock)clock).Now();

        Assert.Equal(new DateOnly(2026, 11, 20), captured.LocalDate);
        Assert.Equal(480, captured.UtcOffsetMinutes);
        Assert.Equal("Asia/Shanghai-Test", captured.TimeZoneId);

        // User travels to UTC-5 later; stored LocalDate must remain 2026-11-20 (ADR-0009).
        clock.LocalTimeZone = newYorkTz;
        var reloaded = OccurredTimestamp.FromStored(
            captured.OccurredAtUtc,
            captured.LocalDate,
            captured.TimeZoneId,
            captured.UtcOffsetMinutes);

        Assert.Equal(new DateOnly(2026, 11, 20), reloaded.LocalDate);
        Assert.Equal(captured.OccurredAtUtc, reloaded.OccurredAtUtc);
    }

    [Fact]
    public void PerceptualHash_DetectsIdenticalAndDistinctFrames()
    {
        var width = 32;
        var height = 32;
        var stride = width * 4;
        var gradientA = new byte[stride * height];
        var invertedB = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var idx = (y * stride) + (x * 4);
                var val = (byte)((x * 255) / width);
                gradientA[idx] = val;
                gradientA[idx + 1] = val;
                gradientA[idx + 2] = val;
                gradientA[idx + 3] = 255;

                var inv = (byte)(255 - val);
                invertedB[idx] = inv;
                invertedB[idx + 1] = inv;
                invertedB[idx + 2] = inv;
                invertedB[idx + 3] = 255;
            }
        }

        var hashA1 = PerceptualHash.ComputeFromBgra32(gradientA, width, height, stride);
        var hashA2 = PerceptualHash.ComputeFromBgra32(gradientA, width, height, stride);
        var hashB = PerceptualHash.ComputeFromBgra32(invertedB, width, height, stride);

        Assert.True(PerceptualHash.IsNearDuplicate(hashA1, hashA2));
        Assert.False(PerceptualHash.IsNearDuplicate(hashA1, hashB));
        Assert.True(PerceptualHash.HammingDistance(hashA1, hashB) > PerceptualHash.DefaultDuplicateThreshold);
    }

    [Fact]
    public void ReportJob_EnforcesExponentialBackoffAndCrashRecovery()
    {
        var t0 = new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero);
        var job = new ReportJob("job_1", new DateOnly(2026, 11, 19), ReportTriggerSource.ScheduledDaily, false, t0);

        job.MarkRunning(t0);
        Assert.Equal(ReportJobStatus.Running, job.Status);
        Assert.Equal(1, job.Attempts);

        // Simulate crash recovery while Running -> returns to Pending
        job.RecoverInterrupted(t0.AddMinutes(2));
        Assert.Equal(ReportJobStatus.Pending, job.Status);

        // Attempt 2 fails -> 5m backoff
        job.MarkRunning(t0.AddMinutes(3));
        job.RecordFailure("Model timeout", t0.AddMinutes(4));
        Assert.Equal(ReportJobStatus.Pending, job.Status);
        Assert.Equal(t0.AddMinutes(9), job.NextAttemptAtUtc);

        // Attempt 3 fails -> terminal Failed
        job.MarkRunning(t0.AddMinutes(10));
        job.RecordFailure("Invalid JSON", t0.AddMinutes(11));
        Assert.Equal(ReportJobStatus.Failed, job.Status);
        Assert.NotNull(job.FinishedAtUtc);
    }

    [Fact]
    public void PrivacyBlacklistPolicy_BlocksBuiltInAndCustomPatterns()
    {
        var policy = new PrivacyBlacklistPolicy();
        policy.UpdateCustomPatterns(["招商银行", "secret-corp"]);

        Assert.True(policy.IsExcluded("1Password.exe", "Vault"));
        Assert.True(policy.IsExcluded("msedge", "工作 - InPrivate 浏览"));
        Assert.True(policy.IsExcluded("chrome", "招商银行个人网银"));
        Assert.False(policy.IsExcluded("Code", "AiAssistant - Program.cs"));
    }

    [Fact]
    public void CapabilityState_DistinguishesOperationalFromBlockedStates()
    {
        Assert.True(CapabilityState.Supported(CapabilityKind.ScreenCapture, "Win32 GDI").IsOperational);
        Assert.True(CapabilityState.Degraded(CapabilityKind.WindowActivity, "Wayland partial").IsOperational);
        Assert.False(CapabilityState.PermissionRequired(CapabilityKind.ScreenCapture, "TCC").IsOperational);
        Assert.False(CapabilityState.DisabledByPolicy(CapabilityKind.AudioCapture, "MDM").IsOperational);
    }
}
