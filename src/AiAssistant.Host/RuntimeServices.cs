using AiAssistant.Application.Ports;
using AiAssistant.Application.UseCases;
using AiAssistant.LocalApi;
using AiAssistant.Models;
using AiAssistant.Platform.macOS.Ipc;
using AiAssistant.Summarization;

namespace AiAssistant.Host;

/// <summary>
/// Typed services resolved once by the composition root. CLI commands and diagnostics consume
/// this record instead of querying <see cref="IServiceProvider"/> ad hoc.
/// </summary>
public sealed record RuntimeServices(
    IClock Clock,
    ILocalMcpControl McpControl,
    IDatabaseMaintenanceService DatabaseMaintenance,
    IDataDeletionService DataDeletion,
    ModelBenchmarkRunner ModelBenchmark,
    IModelGateway ModelGateway,
    EventEmbeddingIndexer EmbeddingIndexer,
    IModelSecretAdmin ModelSecretAdmin,
    RangeReportUseCase RangeReport,
    EvidenceSearchUseCase EvidenceSearch,
    IAsrEngine AsrEngine,
    IWakeWordDetector WakeWordDetector,
    IModelCapabilityProber ModelCapabilityProber,
    IReportDocumentRenderer DocumentRenderer,
    IMacHelperSupervisor? MacHelperSupervisor,
    ICapabilityProvider? Capabilities,
    IImageOcrRecognizer? OcrRecognizer);
