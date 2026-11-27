using System.Runtime.CompilerServices;
using AiAssistant.Application.Policies;
using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;
using AiAssistant.Desktop.ViewModels;
using AiAssistant.Desktop.Views;
using AiAssistant.Testing.Common;
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
    [Fact]
    public async Task DiagnosticsExport_SupportsAllFormatsFromTheSamePreview()
    {
        var renderer = new FakeReportDocumentRenderer();
        var useCase = new DiagnosticsUseCase(
            new FakeCapabilityProvider(),
            new FakeMaintenanceService(),
            new FakeCaptureCoordinator(),
            new FakeReportJobQueue(),
            documentRenderer: renderer);
        var preview = await useCase.BuildSanitizedExportPreviewAsync();
        var directory = TempArtifactDirectory.CreatePath("diag-export-all");

        var export = await useCase.ExportPreviewAsync(directory, preview);

        Assert.True(File.Exists(export.MarkdownPath));
        Assert.True(File.Exists(export.JsonPath));
        Assert.NotNull(export.SvgPath);
        Assert.NotNull(export.PngPath);
        Assert.NotNull(export.PdfPath);
        Assert.True(File.Exists(export.SvgPath));
        Assert.True(File.Exists(export.PngPath));
        Assert.True(File.Exists(export.PdfPath));
        Assert.Contains("本地诊断报告", await File.ReadAllTextAsync(export.MarkdownPath), StringComparison.Ordinal);
        Assert.Contains("平台能力", renderer.LastDocument!.Sections.Select(section => section.Heading));
        Assert.Contains("Headless test platform", await File.ReadAllTextAsync(export.MarkdownPath), StringComparison.Ordinal);

        var svgPath = await useCase.ExportPreviewAsAsync(
            Path.Combine(directory, "single.svg"),
            preview,
            ReportExportFormat.Svg);
        Assert.True(File.Exists(svgPath));
    }

    private sealed class FakeReportDocumentRenderer : IReportDocumentRenderer
    {
        public ReportDocument? LastDocument { get; private set; }

        public string RenderSvg(ReportDocument document)
        {
            LastDocument = document;
            return "<svg/>";
        }

        public byte[] RenderPng(ReportDocument document)
        {
            LastDocument = document;
            return [0x89, 0x50, 0x4E, 0x47];
        }

        public byte[] RenderPdf(ReportDocument document)
        {
            LastDocument = document;
            return "%PDF-fake"u8.ToArray();
        }
    }

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

        public Task<int> PurgeOverQuotaAsync(long maxBytes, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class FakeCapabilityProvider : ICapabilityProvider
    {
        public string PlatformName => "Headless test platform";

        public IReadOnlyList<CapabilityState> GetCapabilities() =>
            [CapabilityState.Supported(CapabilityKind.SystemTray, "Headless tray test")];
    }

    private const string TestBackupKey =
        "AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA-AAAA";

    private sealed class FakeMaintenanceService : IDatabaseMaintenanceService
    {
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<DatabaseHealthReport> CheckHealthAsync(CancellationToken ct) =>
            Task.FromResult(new DatabaseHealthReport(true, SchemaVersion.Current, "test-cipher", "test-vector", 0, 0, "ok"));

        public string CreateBackupKey() => TestBackupKey;

        public Task BackupEncryptedAsync(string destinationPath, string backupKey, CancellationToken ct) => Task.CompletedTask;

        public Task RestoreEncryptedAsync(string backupSourcePath, string backupKey, CancellationToken ct) => Task.CompletedTask;

        public Task StageRestoreAsync(string backupSourcePath, string backupKey, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> CommitStagedRestoreAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<bool> ApplyStagedRestoreAsync(CancellationToken ct) => Task.FromResult(false);

        public Task<bool> DiscardStagedRestoreAsync(CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class RecordingMaintenanceService : IDatabaseMaintenanceService
    {
        public string? BackupPath { get; private set; }
        public string? BackupKey { get; private set; }
        public string? StagedPath { get; private set; }
        public string? StagedKey { get; private set; }
        public bool Committed { get; private set; }
        public bool Discarded { get; private set; }

        /// <summary>Holds the backup open so a test can observe the in-progress UI.</summary>
        public TaskCompletionSource<bool>? BackupGate { get; set; }

        /// <summary>Simulates a backup that cannot be decrypted (wrong key or damaged archive).</summary>
        public Exception? StageRestoreFailure { get; set; }

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<DatabaseHealthReport> CheckHealthAsync(CancellationToken ct) =>
            Task.FromResult(new DatabaseHealthReport(true, SchemaVersion.Current, "test-cipher", "test-vector", 0, 0, "ok"));

        public string CreateBackupKey() => TestBackupKey;

        public async Task BackupEncryptedAsync(string destinationPath, string backupKey, CancellationToken ct)
        {
            BackupPath = destinationPath;
            BackupKey = backupKey;
            if (BackupGate is not null)
            {
                await BackupGate.Task;
            }
        }

        public Task RestoreEncryptedAsync(string backupSourcePath, string backupKey, CancellationToken ct) =>
            Task.CompletedTask;

        public Task StageRestoreAsync(string backupSourcePath, string backupKey, CancellationToken ct)
        {
            if (StageRestoreFailure is not null)
            {
                throw StageRestoreFailure;
            }

            StagedPath = backupSourcePath;
            StagedKey = backupKey;
            return Task.CompletedTask;
        }

        public Task<bool> CommitStagedRestoreAsync(CancellationToken ct)
        {
            Committed = StagedPath is not null;
            return Task.FromResult(Committed);
        }

        public Task<bool> ApplyStagedRestoreAsync(CancellationToken ct) => Task.FromResult(false);

        public Task<bool> DiscardStagedRestoreAsync(CancellationToken ct)
        {
            Discarded = StagedPath is not null;
            StagedPath = null;
            StagedKey = null;
            return Task.FromResult(Discarded);
        }
    }

    private sealed class FakeSaveFilePicker(string path, string extension = "zip") : ISaveFilePicker
    {
        public Task<SaveFileSelection?> PickSaveFileAsync(
            string suggestedFileName,
            IReadOnlyList<SaveFileType> types) =>
            Task.FromResult<SaveFileSelection?>(new SaveFileSelection(path, extension));
    }

    private sealed class FakeOpenFilePicker(string? path) : IOpenFilePicker
    {
        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<SaveFileType> types) =>
            Task.FromResult(path);
    }

    private sealed class FakeVoiceCommandLog : IVoiceCommandLog
    {
        public List<VoiceCommandLogRecord> Records { get; } = [];

        public Task AppendAsync(VoiceCommandLogRecord record, CancellationToken ct)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VoiceCommandLogRecord>> ListRecentAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VoiceCommandLogRecord>>(
                Records.OrderByDescending(record => record.OccurredAtUtc).Take(limit).ToArray());
    }

    private sealed class FakeCommandAudioPlayer : ICommandAudioPlayer
    {
        public string? PlayedContentId { get; private set; }
        public bool Succeeds { get; init; } = true;

        public Task<bool> PlayAsync(string contentId, CancellationToken ct)
        {
            PlayedContentId = contentId;
            return Task.FromResult(Succeeds);
        }
    }

    /// <summary>Keeps each playback pending until the test completes it, mimicking a slow device.</summary>
    private sealed class ControllableCommandAudioPlayer : ICommandAudioPlayer
    {
        private readonly Dictionary<string, TaskCompletionSource<bool>> _pending = new(StringComparer.Ordinal);

        public Task<bool> PlayAsync(string contentId, CancellationToken ct)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[contentId] = completion;
            ct.Register(() => completion.TrySetCanceled(ct));
            return completion.Task;
        }

        public void Complete(string contentId, bool played = true)
        {
            Assert.True(_pending.TryGetValue(contentId, out var completion), $"No pending playback for {contentId}.");
            completion!.TrySetResult(played);
        }
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
        public bool IsActive { get; private set; }
        public bool RecordsCommandAudio { get; private set; }
        public event EventHandler<VoiceSessionState>? StateChanged;
        public event EventHandler<VoiceInteraction>? Interaction;
        public event EventHandler<VoiceSessionFault>? Faulted;

        public Task SetCommandAudioRecordingAsync(bool enabled, CancellationToken ct)
        {
            RecordsCommandAudio = enabled;
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken ct)
        {
            // Mirrors the real controller: enabling only arms wake-word detection and the session
            // stays Sleeping (no StateChanged) until the wake word is detected.
            IsActive = true;
            Interaction?.Invoke(this, new VoiceInteraction(State, "你好时光", string.Empty, "语音已启用，说“你好时光”唤醒。", DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public void SimulateWake()
        {
            State = VoiceSessionState.Listening;
            StateChanged?.Invoke(this, State);
        }

        /// <summary>Mirrors an unexpected session exit: the pump is gone and the user must re-enable.</summary>
        public void SimulateFault(string reason)
        {
            IsActive = false;
            State = VoiceSessionState.Sleeping;
            StateChanged?.Invoke(this, State);
            Faulted?.Invoke(this, new VoiceSessionFault(reason, DateTimeOffset.UtcNow));
        }

        public Task StopAsync(CancellationToken ct)
        {
            IsActive = false;
            if (State != VoiceSessionState.Sleeping)
            {
                State = VoiceSessionState.Sleeping;
                StateChanged?.Invoke(this, State);
            }

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

    private sealed class RecordingRetentionService : IRetentionService
    {
        public int SweepCount { get; private set; }
        public int NextPurged { get; set; }

        public MediaRetentionMetrics GetMetricsSnapshot() => new(SweepCount, NextPurged, DateTimeOffset.UtcNow);

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<int> RunSweepNowAsync(CancellationToken ct)
        {
            SweepCount++;
            return Task.FromResult(NextPurged);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingRetentionSettingsStore(RetentionSettings initial) : IRetentionSettingsStore
    {
        public RetentionSettings Settings { get; private set; } = initial;

        public RetentionSettings Load() => Settings;

        public Task SaveAsync(RetentionSettings settings, CancellationToken ct)
        {
            Settings = settings;
            return Task.CompletedTask;
        }
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

    private sealed class ActionCapabilityProvider : ICapabilityProvider
    {
        public string PlatformName => "Windows";

        public IReadOnlyList<CapabilityState> GetCapabilities() =>
        [
            CapabilityState.PermissionRequired(
                CapabilityKind.ScreenCapture,
                "需要屏幕录制权限",
                "请在系统隐私设置中授予屏幕录制。"),
            CapabilityState.Degraded(CapabilityKind.WakeWord, "语音可选", "请安装语音模型。"),
            CapabilityState.Degraded(CapabilityKind.Ocr, "Windows.Media.Ocr 按需兜底")
        ];
    }

    private sealed class ConfiguredMcpControl : ILocalMcpControl
    {
        public LocalMcpSnapshot GetSnapshot() => LocalMcpSnapshot.Disabled;
        public Task<string> RevealTokenAsync(CancellationToken ct) => Task.FromResult("token");
        public Task<string> RotateTokenAsync(CancellationToken ct) => Task.FromResult("token");
        public Task SetEnabledAsync(bool enabled, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ConfiguredModelsAdmin : IModelConfigurationAdmin
    {
        public ModelConfigurationView Current => new(
            new ModelEndpointView("http://127.0.0.1:11434", "gemma4:12b", "LocalOffline"),
            new ModelEndpointView("http://127.0.0.1:11434", "embeddinggemma-2:740m", "LocalOffline"));
        public Task SaveAsync(ModelConfigurationView update, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingDiagnosticLogReader : IDiagnosticLogReader
    {
        public string? ConfirmedJson { get; private set; }

        public IReadOnlyList<DiagnosticLogEntry> GetRecentEntries(int maxCount = 50) => [];

        public Task ExportConfirmedPreviewAsync(string destinationPath, string confirmedJsonPreview, CancellationToken ct)
        {
            ConfirmedJson = confirmedJsonPreview;
            return Task.CompletedTask;
        }
    }

    private sealed class TwentyJobsQueue : IReportJobQueue
    {
        public int LastRequestedLimit { get; private set; }

        public Task<EnqueueResult> EnqueueAsync(ReportJobRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> RecoverInterruptedJobsAsync(CancellationToken ct) => Task.FromResult(0);

        public Task<ReportJob?> ClaimNextPendingAsync(CancellationToken ct) => Task.FromResult<ReportJob?>(null);

        public Task UpdateAsync(ReportJob job, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<ReportJob>> ListRecentJobsAsync(int limit, CancellationToken ct)
        {
            LastRequestedLimit = limit;
            var jobs = Enumerable.Range(0, 20)
                .Select(index => new ReportJob(
                    $"job-{index}",
                    new DateOnly(2026, 11, 1).AddDays(index),
                    ReportTriggerSource.ScheduledDaily,
                    false,
                    DateTimeOffset.UtcNow,
                    ReportJobStatus.Completed))
                .Cast<ReportJob>()
                .ToArray();
            return Task.FromResult<IReadOnlyList<ReportJob>>(jobs);
        }
    }
}
