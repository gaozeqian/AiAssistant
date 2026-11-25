using System.Runtime.CompilerServices;
using AiAssistant.Application.Policies;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Desktop.ViewModels;
using AiAssistant.Desktop.Views;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace AiAssistant.Desktop.Tests;

[Trait("Category", "UI")]
public sealed partial class DesktopViewModelTests
{
    private sealed class InMemoryEventRepository : IEventRepository
    {
        public List<ActivityEvent> Events { get; } = [];

        public Task AppendBatchAsync(IReadOnlyList<ActivityEvent> events, CancellationToken ct)
        {
            Events.AddRange(events);
            return Task.CompletedTask;
        }

        public Task AppendWithMediaBatchAsync(IReadOnlyList<EventWithMediaWrite> items, CancellationToken ct) =>
            AppendBatchAsync(items.Select(item => item.Event).ToArray(), ct);

        public async IAsyncEnumerable<ActivityEvent> QueryAsync(
            EventQuery query,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            var matches = Events.Where(item => item.Timestamp.LocalDate == query.LocalDate);
            if (query.Before is { } cursor)
            {
                matches = matches.Where(item =>
                    item.Timestamp.OccurredAtUtc < cursor.OccurredAtUtc ||
                    (item.Timestamp.OccurredAtUtc == cursor.OccurredAtUtc &&
                     string.CompareOrdinal(item.Id, cursor.EventId) < 0));
            }

            matches = query.Descending
                ? matches.OrderByDescending(item => item.Timestamp.OccurredAtUtc).ThenByDescending(item => item.Id, StringComparer.Ordinal)
                : matches.OrderBy(item => item.Timestamp.OccurredAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal);
            var page = matches.Take(Math.Max(1, query.Limit));
            foreach (var item in page)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }
        }

        public Task<ActivityEvent?> GetByIdAsync(string eventId, CancellationToken ct) =>
            Task.FromResult(Events.FirstOrDefault(item => item.Id == eventId));

        public Task<IReadOnlyList<DateOnly>> ListActiveDatesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DateOnly>>(Events.Select(item => item.Timestamp.LocalDate).Distinct().ToArray());

        public Task<bool> DeleteEventAsync(string eventId, CancellationToken ct) => Task.FromResult(false);

        public Task<int> PurgeDateAsync(DateOnly localDate, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class FakeMediaStore : IMediaStore
    {
        private static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        private readonly Dictionary<string, TaskCompletionSource<byte[]?>> _pending = new(StringComparer.Ordinal);

        public bool DelayReads { get; init; }

        public int ReadCount { get; private set; }

        public string? LastReadContentId { get; private set; }

        public Task<ContentRef> PutAsync(ContentWriteRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<byte[]?> ReadDecryptedAsync(string contentId, CancellationToken ct)
        {
            ReadCount++;
            LastReadContentId = contentId;
            if (!DelayReads)
            {
                return Task.FromResult<byte[]?>(Png.ToArray());
            }

            var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[contentId] = completion;
            return completion.Task.WaitAsync(ct);
        }

        public void CompleteRead(string contentId) => _pending[contentId].TrySetResult(Png.ToArray());

        public Task<int> IncrementRefAsync(string contentId, CancellationToken ct) => Task.FromResult(1);

        public Task<int> DecrementRefAsync(string contentId, CancellationToken ct) => Task.FromResult(0);

        public Task<int> PurgeExpiredAsync(DateTimeOffset nowUtc, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class FakeCapabilityProvider : ICapabilityProvider
    {
        public string PlatformName => "Headless test platform";

        public IReadOnlyList<CapabilityState> GetCapabilities() =>
            [CapabilityState.Supported(CapabilityKind.SystemTray, "Headless tray test")];
    }

    private sealed class FakeMaintenanceService : IDatabaseMaintenanceService
    {
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<DatabaseHealthReport> CheckHealthAsync(CancellationToken ct) =>
            Task.FromResult(new DatabaseHealthReport(true, SchemaVersion.Current, "test-cipher", "test-vector", 0, 0, "ok"));

        public Task BackupEncryptedAsync(string destinationPath, CancellationToken ct) => Task.CompletedTask;

        public Task RestoreEncryptedAsync(string backupSourcePath, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeReportJobQueue : IReportJobQueue
    {
        public Task<EnqueueResult> EnqueueAsync(ReportJobRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> RecoverInterruptedJobsAsync(CancellationToken ct) => Task.FromResult(0);

        public Task<ReportJob?> ClaimNextPendingAsync(CancellationToken ct) => Task.FromResult<ReportJob?>(null);

        public Task UpdateAsync(ReportJob job, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<ReportJob>> ListRecentJobsAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReportJob>>([]);
    }

    private sealed class FakeReportRepository : IReportRepository
    {
        private readonly List<DailyReport> _reports = [];

        public Task<DailyReport?> GetLatestAsync(DateOnly localDate, CancellationToken ct) =>
            Task.FromResult(_reports.Where(report => report.LocalDate == localDate)
                .OrderByDescending(report => report.Version)
                .FirstOrDefault());

        public Task<IReadOnlyList<DailyReport>> ListVersionsAsync(DateOnly localDate, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DailyReport>>(_reports.Where(report => report.LocalDate == localDate)
                .OrderByDescending(report => report.Version)
                .ToArray());

        public Task<DailyReport> AppendAsync(
            DateOnly localDate,
            string markdown,
            string statsJson,
            string modelId,
            ModelEndpointTier endpointTier,
            bool redactionApplied,
            ReportTriggerSource triggerSource,
            DateTimeOffset generatedAtUtc,
            CancellationToken ct)
        {
            var report = new DailyReport(
                $"rpt_{_reports.Count + 1}",
                localDate,
                _reports.Count + 1,
                markdown,
                statsJson,
                modelId,
                endpointTier,
                redactionApplied,
                triggerSource,
                generatedAtUtc);
            _reports.Add(report);
            return Task.FromResult(report);
        }
    }

    private sealed class FakeCaptureCoordinator : ICaptureCoordinator
    {
        public PrivacyBlacklistPolicy Policy { get; } = new();

        public bool IsPaused { get; private set; }

        public IReadOnlyList<string> BlacklistedPatterns => Policy.GetEffectivePatterns();

        public int SnapshotCount { get; private set; }

        public event EventHandler<CapturePipelineMetrics>? MetricsUpdated;

        public CapturePipelineMetrics GetMetricsSnapshot() => new(IsPaused, 0, 0, 0, 0, 0, 0);

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task PauseAsync(string reason, CancellationToken ct)
        {
            IsPaused = true;
            MetricsUpdated?.Invoke(this, GetMetricsSnapshot());
            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken ct)
        {
            IsPaused = false;
            MetricsUpdated?.Invoke(this, GetMetricsSnapshot());
            return Task.CompletedTask;
        }

        public Task SetBlacklistedPatternsAsync(IReadOnlyList<string> patterns, CancellationToken ct)
        {
            Policy.UpdateCustomPatterns(patterns);
            return Task.CompletedTask;
        }

        public Task SubmitSampleAsync(CapturedSample sample, CancellationToken ct) => Task.CompletedTask;

        public Task CaptureImmediateSnapshotAsync(CancellationToken ct)
        {
            SnapshotCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAutoStartService : IAutoStartService
    {
        private bool _enabled;

        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.AutoStart, "Fake");

        public Task<bool> IsEnabledAsync(CancellationToken ct) => Task.FromResult(_enabled);

        public Task SetEnabledAsync(bool enabled, CancellationToken ct)
        {
            _enabled = enabled;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeVoiceSession : IVoiceSession
    {
        public VoiceSessionState State { get; private set; } = VoiceSessionState.Sleeping;
        public CapabilityState Capability => CapabilityState.Supported(CapabilityKind.WakeWord, "fake KWS");
        public bool RecordsCommandAudio { get; private set; }
        public event EventHandler<VoiceSessionState>? StateChanged;
        public event EventHandler<VoiceInteraction>? Interaction;

        public Task SetCommandAudioRecordingAsync(bool enabled, CancellationToken ct)
        {
            RecordsCommandAudio = enabled;
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken ct)
        {
            State = VoiceSessionState.Listening;
            StateChanged?.Invoke(this, State);
            Interaction?.Invoke(this, new VoiceInteraction(State, "你好时光", string.Empty, "我在听", DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            State = VoiceSessionState.Sleeping;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeDataDeletionService : IDataDeletionService
    {
        public int Calls { get; private set; }

        public Task<DataDeletionResult> PurgeDateAsync(DateOnly localDate, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new DataDeletionResult(1, 1, 1, 0, 1));
        }

        public Task<DataDeletionResult> PurgeAllAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new DataDeletionResult(2, 2, 2, 2, 2));
        }
    }

    private sealed class FakeEvidenceSearch(EvidenceReference evidence) : IEvidenceSearch
    {
        public Task<SearchQueryResult> SearchAsync(
            string query,
            DateOnly fromDate,
            DateOnly toDate,
            CancellationToken ct) =>
            Task.FromResult(new SearchQueryResult(query, fromDate, toDate, [evidence], 1, 0));
    }

    private sealed class FakeAnswerer : IEvidenceQuestionAnswerer
    {
        public Task<EvidenceQaAnswer> AnswerAsync(
            string question,
            DateOnly fromDate,
            DateOnly toDate,
            CancellationToken ct) =>
            Task.FromResult(new EvidenceQaAnswer(
                question,
                "没有找到可验证的本地活动证据，因此不生成推测性回答。",
                [],
                [],
                "none",
                ModelEndpointTier.LocalOffline,
                true,
                "no-evidence"));
    }

    private sealed class FakeRangeReportService : IRangeReportService
    {
        public Task<RangeReport> GenerateAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct) =>
            Task.FromResult(new RangeReport(
                fromDate,
                toDate,
                "# 区间报告",
                "{}",
                [],
                "none",
                true,
                "no-evidence"));
    }

    private sealed class FakeRetentionService : IRetentionService
    {
        public MediaRetentionMetrics GetMetricsSnapshot() => new(2, 3, DateTimeOffset.UtcNow);
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<int> RunSweepNowAsync(CancellationToken ct) => Task.FromResult(0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeDiagnosticLogReader : IDiagnosticLogReader
    {
        public IReadOnlyList<DiagnosticLogEntry> GetRecentEntries(int maxCount = 50) =>
        [
            new DiagnosticLogEntry(DateTimeOffset.UtcNow, "Information", "AppRuntime", "Runtime initialized cleanly.")
        ];

        public Task ExportConfirmedPreviewAsync(string destinationPath, string confirmedJsonPreview, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
