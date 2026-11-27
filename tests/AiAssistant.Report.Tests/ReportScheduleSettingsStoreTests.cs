using AiAssistant.Application.Ports;
using AiAssistant.Scheduler;
using AiAssistant.Testing.Common;

namespace AiAssistant.Report.Tests;

[Trait("Category", "Unit")]
public sealed class ReportScheduleSettingsStoreTests
{
    [Fact]
    public async Task Store_RoundTripsSettingsAcrossInstances()
    {
        var directory = TempArtifactDirectory.CreatePath("report-schedule");
        try
        {
            var store = new ReportScheduleSettingsStore(directory);
            await store.SaveAsync(
                new ReportScheduleSettings(new TimeOnly(2, 15), Enabled: false),
                CancellationToken.None);

            var reloaded = new ReportScheduleSettingsStore(directory);
            var settings = reloaded.Load();

            Assert.Equal(new TimeOnly(2, 15), settings.DailyLocalTime);
            Assert.False(settings.Enabled);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void DefaultPolicy_ChecksPreviousDayAtMidnightThirty()
    {
        Assert.Equal(new TimeOnly(0, 30), ReportScheduleSettings.Default.DailyLocalTime);
        Assert.True(ReportScheduleSettings.Default.Enabled);
    }
}
