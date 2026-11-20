namespace AiAssistant.Core.Domain;

public enum ReportTriggerSource
{
    Manual = 0,
    StartupCatchUp = 1,
    ScheduledDaily = 2,
    VoiceCommand = 3
}

public enum ReportJobStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    SkippedAlreadyExists = 3,
    Failed = 4
}

/// <summary>
/// Persistent report generation job with bounded exponential backoff retry and crash recovery semantics (ADR-0010).
/// </summary>
public sealed class ReportJob
{
    public const int MaxAttempts = 3;

    public string Id { get; }
    public DateOnly TargetDate { get; }
    public ReportTriggerSource TriggerSource { get; }
    public bool ForceOverwrite { get; }
    public ReportJobStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }
    public DateTimeOffset? FinishedAtUtc { get; private set; }

    public ReportJob(
        string id,
        DateOnly targetDate,
        ReportTriggerSource triggerSource,
        bool forceOverwrite,
        DateTimeOffset createdAtUtc,
        ReportJobStatus status = ReportJobStatus.Pending,
        int attempts = 0,
        string? lastError = null,
        DateTimeOffset? nextAttemptAtUtc = null,
        DateTimeOffset? finishedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        TargetDate = targetDate;
        TriggerSource = triggerSource;
        ForceOverwrite = forceOverwrite;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        Status = status;
        Attempts = attempts;
        LastError = lastError;
        NextAttemptAtUtc = nextAttemptAtUtc?.ToUniversalTime();
        FinishedAtUtc = finishedAtUtc?.ToUniversalTime();
    }

    public void MarkRunning(DateTimeOffset nowUtc)
    {
        if (Status is not (ReportJobStatus.Pending or ReportJobStatus.Running))
        {
            throw new InvalidOperationException($"Cannot start job {Id} in status {Status}.");
        }

        Status = ReportJobStatus.Running;
        Attempts++;
        NextAttemptAtUtc = null;
        _ = nowUtc;
    }

    public void MarkCompleted(DateTimeOffset nowUtc)
    {
        Status = ReportJobStatus.Completed;
        LastError = null;
        NextAttemptAtUtc = null;
        FinishedAtUtc = nowUtc.ToUniversalTime();
    }

    public void MarkSkippedAlreadyExists(DateTimeOffset nowUtc)
    {
        Status = ReportJobStatus.SkippedAlreadyExists;
        LastError = null;
        NextAttemptAtUtc = null;
        FinishedAtUtc = nowUtc.ToUniversalTime();
    }

    public void RecordFailure(string diagnosticError, DateTimeOffset nowUtc)
    {
        LastError = string.IsNullOrWhiteSpace(diagnosticError) ? "Unknown error" : diagnosticError;
        var utc = nowUtc.ToUniversalTime();
        if (Attempts >= MaxAttempts)
        {
            Status = ReportJobStatus.Failed;
            NextAttemptAtUtc = null;
            FinishedAtUtc = utc;
            return;
        }

        Status = ReportJobStatus.Pending;
        var backoffMinutes = Attempts switch
        {
            1 => 1,
            2 => 5,
            _ => 15
        };
        NextAttemptAtUtc = utc.AddMinutes(backoffMinutes);
    }

    public void RecoverInterrupted(DateTimeOffset nowUtc)
    {
        if (Status != ReportJobStatus.Running)
        {
            return;
        }

        if (Attempts >= MaxAttempts)
        {
            Status = ReportJobStatus.Failed;
            LastError = "Process exited abnormally while running job (max attempts exhausted).";
            FinishedAtUtc = nowUtc.ToUniversalTime();
        }
        else
        {
            Status = ReportJobStatus.Pending;
            LastError = "Recovered after abnormal process exit.";
            NextAttemptAtUtc = nowUtc.ToUniversalTime();
        }
    }
}
