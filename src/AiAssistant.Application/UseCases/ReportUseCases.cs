using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;

namespace AiAssistant.Application.UseCases;

public sealed record ReportRequestTicket(string JobId, bool DeduplicatedExistingJob);

public sealed class ReportUseCase(
    IReportJobQueue jobQueue,
    IReportRepository reportRepository,
    IClock clock)
{
    public Task<DailyReport?> GetLatestAsync(DateOnly date, CancellationToken ct = default) =>
        reportRepository.GetLatestAsync(date, ct);

    public Task<IReadOnlyList<DailyReport>> ListVersionsAsync(DateOnly date, CancellationToken ct = default) =>
        reportRepository.ListVersionsAsync(date, ct);

    public async Task<ReportRequestTicket> RequestAsync(
        DateOnly date,
        bool forceOverwrite,
        CancellationToken ct = default)
    {
        var result = await jobQueue.EnqueueAsync(
            new ReportJobRequest(date, ReportTriggerSource.Manual, forceOverwrite),
            ct);
        return new ReportRequestTicket(result.JobId, result.DeduplicatedExistingPendingJob);
    }

    /// <summary>Waits for the persisted job to finish so the UI can display the exact new version.</summary>
    public async Task<DailyReport?> RequestAndWaitAsync(
        DateOnly date,
        bool forceOverwrite,
        CancellationToken ct = default)
    {
        var before = await reportRepository.GetLatestAsync(date, ct);
        var ticket = await RequestAsync(date, forceOverwrite, ct);
        var deadline = clock.UtcNow.AddMinutes(5);
        while (clock.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var jobs = await jobQueue.ListRecentJobsAsync(50, ct);
            var job = jobs.FirstOrDefault(item => item.Id == ticket.JobId);
            if (job is { Status: ReportJobStatus.Failed })
            {
                throw new InvalidOperationException(job.LastError ?? "Report generation failed.");
            }

            var latest = await reportRepository.GetLatestAsync(date, ct);
            if (latest is not null &&
                (before is null || latest.Id != before.Id || latest.Version > before.Version))
            {
                return latest;
            }

            if (job is { Status: ReportJobStatus.SkippedAlreadyExists })
            {
                return latest;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        throw new TimeoutException("Report generation did not complete within 5 minutes.");
    }

    public static async Task<string> ExportMarkdownAsync(
        DailyReport report,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var fileName = $"AiAssistant-日报-{report.LocalDate:yyyyMMdd}-v{report.Version}.md";
        var path = Path.Combine(destinationDirectory, fileName);
        await File.WriteAllTextAsync(path, report.Markdown, ct);
        return path;
    }

    /// <summary>Writes the daily report markdown to the exact destination path chosen by the user.</summary>
    public static async Task<string> ExportMarkdownToAsync(
        DailyReport report,
        string destinationPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        await File.WriteAllTextAsync(destinationPath, report.Markdown, ct);
        return destinationPath;
    }
}
