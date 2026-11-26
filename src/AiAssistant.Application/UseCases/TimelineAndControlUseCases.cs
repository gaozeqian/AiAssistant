using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Domain;
using AiAssistant.Core.ValueObjects;

namespace AiAssistant.Application.UseCases;

/// <summary>
/// One page of a day's timeline, ordered newest→oldest to match the list (first row is the latest
/// activity). <see cref="HasMore"/> means older records exist below this page.
/// </summary>
public sealed record TimelinePage(IReadOnlyList<ActivityEvent> Events, bool HasMore);

/// <summary>
/// Application use case for querying timeline events, on-demand decrypted media, and deleting user data.
/// Desktop ViewModels depend only on this and sibling use cases—never directly on Storage or Platform.
/// </summary>
public sealed class TimelineUseCase(
    IEventRepository eventRepository,
    IMediaStore mediaStore,
    IClock clock)
{
    /// <summary>Default page size; a busy day can exceed ten thousand events, so the list pages instead of truncating.</summary>
    public const int DefaultPageSize = 200;

    public DateOnly GetTodayLocalDate() => clock.Now().LocalDate;

    /// <summary>
    /// Loads one newest-first page of a date. <paramref name="before"/> is the last row already shown;
    /// passing it returns the next older slice without being disturbed by events captured in between.
    /// </summary>
    public async Task<TimelinePage> GetEventsForDateAsync(
        DateOnly localDate,
        string? keyword = null,
        EventCursor? before = null,
        int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        var size = Math.Clamp(pageSize, 1, 2_000);
        // Fetch one extra row to detect "older records exist" without a second COUNT query.
        var query = new EventQuery(
            LocalDate: localDate,
            SearchKeyword: keyword,
            Limit: size + 1,
            Descending: true,
            Before: before);

        var list = new List<ActivityEvent>(size + 1);
        await foreach (var item in eventRepository.QueryAsync(query, ct))
        {
            list.Add(item);
        }

        var hasMore = list.Count > size;
        if (hasMore)
        {
            list.RemoveAt(list.Count - 1);
        }

        // Already newest-first: the list shows the latest activity at the top.
        return new TimelinePage(list, hasMore);
    }

    public Task<IReadOnlyList<DateOnly>> ListActiveDatesAsync(CancellationToken ct = default) =>
        eventRepository.ListActiveDatesAsync(ct);

    public Task<byte[]?> LoadDecryptedMediaAsync(string contentId, CancellationToken ct = default) =>
        mediaStore.ReadDecryptedAsync(contentId, ct);

    public Task<bool> DeleteEventAsync(string eventId, CancellationToken ct = default) =>
        eventRepository.DeleteEventAsync(eventId, ct);

    public Task<int> PurgeDateAsync(DateOnly localDate, CancellationToken ct = default) =>
        eventRepository.PurgeDateAsync(localDate, ct);
}

public sealed class CaptureControlUseCase(
    ICaptureCoordinator coordinator,
    IAutoStartService autoStartService)
{
    public bool IsPaused => coordinator.IsPaused;
    public IReadOnlyList<string> BlacklistedPatterns => coordinator.BlacklistedPatterns;
    public event EventHandler<CapturePipelineMetrics>? MetricsUpdated
    {
        add => coordinator.MetricsUpdated += value;
        remove => coordinator.MetricsUpdated -= value;
    }

    public CapturePipelineMetrics GetMetrics() => coordinator.GetMetricsSnapshot();

    public Task TogglePauseAsync(CancellationToken ct = default) =>
        coordinator.IsPaused
            ? coordinator.ResumeAsync(ct)
            : coordinator.PauseAsync("User toggled pause", ct);

    public Task PauseAsync(string reason, CancellationToken ct = default) =>
        coordinator.PauseAsync(reason, ct);

    public Task ResumeAsync(CancellationToken ct = default) =>
        coordinator.ResumeAsync(ct);

    public Task UpdateBlacklistAsync(IReadOnlyList<string> patterns, CancellationToken ct = default) =>
        coordinator.SetBlacklistedPatternsAsync(patterns, ct);

    public Task CaptureSnapshotNowAsync(CancellationToken ct = default) =>
        coordinator.CaptureImmediateSnapshotAsync(ct);

    public CapabilityState AutoStartCapability => autoStartService.Capability;
    public Task<bool> IsAutoStartEnabledAsync(CancellationToken ct = default) => autoStartService.IsEnabledAsync(ct);
    public Task SetAutoStartEnabledAsync(bool enabled, CancellationToken ct = default) => autoStartService.SetEnabledAsync(enabled, ct);
}

public sealed record SystemDiagnosticsSnapshot(
    string PlatformName,
    DatabaseHealthReport DatabaseHealth,
    CapturePipelineMetrics PipelineMetrics,
    IReadOnlyList<CapabilityState> Capabilities,
    IReadOnlyList<ReportJob> RecentJobs,
    MediaRetentionMetrics? RetentionMetrics = null,
    IReadOnlyList<DiagnosticLogEntry>? RecentLogs = null,
    ModelCapabilitySnapshot? ModelCapability = null,
    IReadOnlyList<ModelAuditEntry>? ModelAudit = null,
    IReadOnlyList<ModelEndpointCapability>? ModelEndpoints = null);

public sealed record DiagnosticExport(
    string MarkdownPath,
    string JsonPath,
    string? SvgPath = null,
    string? PngPath = null,
    string? PdfPath = null);

public sealed class DiagnosticsUseCase(
    ICapabilityProvider capabilityProvider,
    IDatabaseMaintenanceService maintenanceService,
    ICaptureCoordinator coordinator,
    IReportJobQueue jobQueue,
    IRetentionService? retentionService = null,
    IDiagnosticLogReader? logReader = null,
    IModelGateway? modelGateway = null,
    IModelAuditSink? modelAuditSink = null,
    IModelCapabilityProber? modelCapabilityProber = null,
    IClock? clock = null,
    IReportDocumentRenderer? documentRenderer = null)
{
    private static readonly JsonSerializerOptions PreviewJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly IClock _clock = clock ?? new SystemClock();

    public async Task<SystemDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var dbHealth = await maintenanceService.CheckHealthAsync(ct);
        var jobs = await jobQueue.ListRecentJobsAsync(20, ct);
        var retention = retentionService?.GetMetricsSnapshot();
        var recentLogs = logReader?.GetRecentEntries(30) ?? [];
        ModelCapabilitySnapshot? modelCapability = null;
        if (modelGateway is not null)
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                modelCapability = await modelGateway.ProbeAsync(probeCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                modelCapability = new ModelCapabilitySnapshot(
                    false,
                    false,
                    modelGateway.ChatModelId,
                    modelGateway.EmbeddingModelId,
                    ModelEndpointTier.LocalOffline,
                    "Model probe timed out.");
            }
        }

        IReadOnlyList<ModelEndpointCapability>? modelEndpoints = null;
        if (modelCapabilityProber is not null)
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                modelEndpoints = await modelCapabilityProber.ProbeAllAsync(forceRefresh: false, probeCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                modelEndpoints = null;
            }
        }

        return new SystemDiagnosticsSnapshot(
            capabilityProvider.PlatformName,
            dbHealth,
            coordinator.GetMetricsSnapshot(),
            capabilityProvider.GetCapabilities(),
            jobs,
            retention,
            recentLogs,
            modelCapability,
            modelAuditSink?.GetRecent(20),
            modelEndpoints);
    }

    /// <summary>
    /// Builds a sanitized, metadata-only JSON preview so the user can inspect every field before exporting (implementation.md §7.3).
    /// </summary>
    public async Task<string> BuildSanitizedExportPreviewAsync(CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct);
        var payload = new
        {
            exportedAtUtc = _clock.UtcNow,
            platform = snapshot.PlatformName,
            schemaVersion = snapshot.DatabaseHealth.SchemaVersion.Number,
            database = new
            {
                isHealthy = snapshot.DatabaseHealth.IsHealthy,
                cipher = snapshot.DatabaseHealth.CipherDescription,
                vectorBackend = snapshot.DatabaseHealth.VectorBackend,
                eventCount = snapshot.DatabaseHealth.EventCount,
                encryptedBlobCount = snapshot.DatabaseHealth.EncryptedBlobCount
            },
            capturePipeline = snapshot.PipelineMetrics,
            retention = snapshot.RetentionMetrics,
            capabilities = snapshot.Capabilities.Select(c => new
            {
                kind = c.Kind.ToString(),
                status = c.Status.ToString(),
                summary = c.Summary
            }),
            recentJobs = snapshot.RecentJobs.Select(j => new
            {
                targetDate = j.TargetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                status = j.Status.ToString(),
                attempts = j.Attempts
            }),
            recentSanitizedLogs = snapshot.RecentLogs ?? []
        };

        return JsonSerializer.Serialize(payload, PreviewJsonOptions);
    }

    public async Task ExportConfirmedPreviewAsync(string destinationPath, string confirmedJsonPreview, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmedJsonPreview);

        if (logReader is not null)
        {
            await logReader.ExportConfirmedPreviewAsync(destinationPath, confirmedJsonPreview, ct);
            return;
        }

        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(destinationPath, confirmedJsonPreview, ct);
    }

    /// <summary>Writes every available diagnostics format into the directory.</summary>
    public async Task<DiagnosticExport> ExportPreviewAsync(
        string destinationDirectory,
        string confirmedJsonPreview,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmedJsonPreview);
        Directory.CreateDirectory(destinationDirectory);
        var stem = string.Create(
            CultureInfo.InvariantCulture,
            $"AiAssistant-诊断导出-{_clock.LocalNow:yyyyMMdd-HHmmss}");
        var markdownPath = Path.Combine(destinationDirectory, stem + ".md");
        var jsonPath = Path.Combine(destinationDirectory, stem + ".json");
        var document = BuildDiagnosticDocument(confirmedJsonPreview);
        await File.WriteAllTextAsync(markdownPath, ReportDocumentMarkdownRenderer.Render(document), ct);
        await ExportConfirmedPreviewAsync(jsonPath, confirmedJsonPreview, ct);

        string? svgPath = null;
        string? pngPath = null;
        string? pdfPath = null;
        if (documentRenderer is not null)
        {
            svgPath = Path.Combine(destinationDirectory, stem + ".svg");
            await File.WriteAllTextAsync(svgPath, documentRenderer.RenderSvg(document), ct);
            pngPath = Path.Combine(destinationDirectory, stem + ".png");
            await File.WriteAllBytesAsync(pngPath, documentRenderer.RenderPng(document), ct);
            pdfPath = Path.Combine(destinationDirectory, stem + ".pdf");
            await File.WriteAllBytesAsync(pdfPath, documentRenderer.RenderPdf(document), ct);
        }

        return new DiagnosticExport(markdownPath, jsonPath, svgPath, pngPath, pdfPath);
    }

    /// <summary>Writes one diagnostics format to the exact destination path chosen by the user.</summary>
    public async Task<string> ExportPreviewAsAsync(
        string destinationPath,
        string confirmedJsonPreview,
        ReportExportFormat format,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmedJsonPreview);
        if (format == ReportExportFormat.Json)
        {
            await ExportConfirmedPreviewAsync(destinationPath, confirmedJsonPreview, ct);
            return destinationPath;
        }

        var document = BuildDiagnosticDocument(confirmedJsonPreview);
        switch (format)
        {
            case ReportExportFormat.Markdown:
                await File.WriteAllTextAsync(
                    destinationPath,
                    ReportDocumentMarkdownRenderer.Render(document),
                    ct);
                break;
            case ReportExportFormat.Svg:
                await File.WriteAllTextAsync(
                    destinationPath,
                    RequireDocumentRenderer().RenderSvg(document),
                    ct);
                break;
            case ReportExportFormat.Png:
                await File.WriteAllBytesAsync(
                    destinationPath,
                    RequireDocumentRenderer().RenderPng(document),
                    ct);
                break;
            case ReportExportFormat.Pdf:
                await File.WriteAllBytesAsync(
                    destinationPath,
                    RequireDocumentRenderer().RenderPdf(document),
                    ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        return destinationPath;
    }

    private IReportDocumentRenderer RequireDocumentRenderer() =>
        documentRenderer ?? throw new InvalidOperationException("SVG/PNG/PDF 导出需要文档渲染器。");

    private static ReportDocument BuildDiagnosticDocument(string json)
    {
        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            var platform = ReadString(root, "platform") ?? "未知平台";
            var exportedAt = ReadString(root, "exportedAtUtc") ?? string.Empty;
            var sections = new List<ReportSection>();
            var overview = new List<ReportField>();
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.Object:
                        sections.Add(new ReportFieldSection(
                            Label(property.Name),
                            FlattenFields(property.Value)));
                        break;
                    case JsonValueKind.Array:
                        sections.Add(new ReportBulletSection(
                            Label(property.Name),
                            FormatArray(property.Value)));
                        break;
                    default:
                        overview.Add(new ReportField(Label(property.Name), FormatValue(property.Value)));
                        break;
                }
            }

            if (overview.Count > 0)
            {
                sections.Insert(0, new ReportFieldSection("概要", overview));
            }

            return new ReportDocument(
                "本地诊断报告",
                $"平台 {platform} · 导出时间 {exportedAt} · 仅含脱敏元数据",
                sections);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new ReportDocument(
                "本地诊断报告",
                "预览内容不是有效 JSON",
                [new ReportCodeSection("原始预览", json)]);
        }
    }

    private static List<ReportField> FlattenFields(JsonElement element, string prefix = "")
    {
        var fields = new List<ReportField>();
        foreach (var property in element.EnumerateObject())
        {
            var label = string.IsNullOrEmpty(prefix)
                ? Label(property.Name)
                : $"{prefix}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                fields.AddRange(FlattenFields(property.Value, label));
            }
            else
            {
                fields.Add(new ReportField(label, FormatValue(property.Value)));
            }
        }

        return fields;
    }

    private static List<string> FormatArray(JsonElement array)
    {
        var items = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            items.Add(item.ValueKind == JsonValueKind.Object
                ? FormatObject(item)
                : FormatValue(item));
        }

        return items.Count == 0 ? ["（无）"] : items;
    }

    private static string FormatObject(JsonElement element) =>
        string.Join(
            " · ",
            element.EnumerateObject().Select(property =>
                $"{Label(property.Name)}={FormatValue(property.Value)}"));

    private static string FormatValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "是",
        JsonValueKind.False => "否",
        JsonValueKind.Null or JsonValueKind.Undefined => "—",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Object => FormatObject(value),
        JsonValueKind.Array => string.Join("、", value.EnumerateArray().Select(FormatValue)),
        _ => value.GetRawText()
    };

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Label(string name) => name switch
    {
        "exportedAtUtc" => "导出时间",
        "platform" => "平台",
        "schemaVersion" => "Schema 版本",
        "database" => "数据库",
        "capturePipeline" => "采集管道",
        "retention" => "保留策略",
        "capabilities" => "平台能力",
        "recentJobs" => "最近任务",
        "recentSanitizedLogs" => "脱敏日志",
        "isHealthy" => "健康",
        "cipher" => "加密",
        "vectorBackend" => "向量后端",
        "eventCount" => "事件数",
        "encryptedBlobCount" => "加密媒体数",
        "kind" => "能力",
        "status" => "状态",
        "summary" => "说明",
        "targetDate" => "日期",
        "attempts" => "尝试次数",
        "timestampUtc" or "TimestampUtc" => "时间",
        "level" or "Level" => "级别",
        "category" or "Category" => "类别",
        "sanitizedMessage" or "SanitizedMessage" => "消息",
        _ => name
    };
}
