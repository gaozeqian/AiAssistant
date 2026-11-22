namespace AiAssistant.Core.Configuration;

/// <summary>
/// Every environment variable the product reads, in one place (R9).
/// Code must reference these constants instead of string literals so the audit can prove
/// the variable set is closed and matches docs/reference/environment-variables.md.
/// </summary>
public static class EnvironmentNames
{
    // 数据与路径
    public const string ArtifactsRoot = "AIASSISTANT_ARTIFACTS_ROOT";
    public const string DataDirectory = "AIASSISTANT_DATA_DIRECTORY";
    public const string CaptureDirectories = "AIASSISTANT_CAPTURE_DIRECTORIES";
    public const string ModelsConfig = "AIASSISTANT_MODELS_CONFIG";
    public const string SherpaModelDir = "AIASSISTANT_SHERPA_MODEL_DIR";
    public const string WhisperModelPath = "AIASSISTANT_WHISPER_MODEL_PATH";
    public const string RapidOcrModelDir = "AIASSISTANT_RAPIDOCR_MODEL_DIR";
    public const string MacHelperPath = "AIASSISTANT_MAC_HELPER_PATH";
    public const string PdfFontPath = "AIASSISTANT_PDF_FONT_PATH";
    public const string IpcToken = "AIASSISTANT_IPC_TOKEN";

    // 模型网关
    public const string ModelProvider = "AIASSISTANT_MODEL_PROVIDER";
    public const string ModelBaseUrl = "AIASSISTANT_MODEL_BASE_URL";
    public const string ChatModel = "AIASSISTANT_CHAT_MODEL";
    public const string EmbeddingModel = "AIASSISTANT_EMBEDDING_MODEL";
    public const string EmbeddingQueryPrefix = "AIASSISTANT_EMBEDDING_QUERY_PREFIX";
    public const string EmbeddingDocumentStyle = "AIASSISTANT_EMBEDDING_DOCUMENT_STYLE";
    public const string ModelThink = "AIASSISTANT_MODEL_THINK";
    public const string ModelThinkingLevel = "AIASSISTANT_MODEL_THINKING_LEVEL";
    public const string ModelApiKey = "AIASSISTANT_MODEL_API_KEY";
    public const string ModelAllowRemote = "AIASSISTANT_MODEL_ALLOW_REMOTE";

    // 语音
    public const string AsrEngine = "AIASSISTANT_ASR_ENGINE";
    public const string KwsScore = "AIASSISTANT_KWS_SCORE";
    public const string KwsThreshold = "AIASSISTANT_KWS_THRESHOLD";
    public const string VoiceDisableChime = "AIASSISTANT_VOICE_DISABLE_CHIME";
    public const string VoiceRecordCommandAudio = "AIASSISTANT_VOICE_RECORD_COMMAND_AUDIO";
    public const string VoiceAudioRetentionDays = "AIASSISTANT_VOICE_AUDIO_RETENTION_DAYS";

    // 报告
    public const string ReportTime = "AIASSISTANT_REPORT_TIME";
    public const string ReportDisableSchedule = "AIASSISTANT_REPORT_DISABLE_SCHEDULE";

    // 本地 MCP
    public const string McpEnabled = "AIASSISTANT_MCP_ENABLED";
    public const string McpPort = "AIASSISTANT_MCP_PORT";
    public const string McpRpm = "AIASSISTANT_MCP_RPM";

    // UI
    public const string Theme = "AIASSISTANT_THEME";
    public const string UiE2E = "AIASSISTANT_UI_E2E";
    public const string UiE2EShow = "AIASSISTANT_UI_E2E_SHOW";
    public const string UiE2EOutput = "AIASSISTANT_UI_E2E_OUTPUT";

    // 测试与诊断
    public const string SoakDays = "AIASSISTANT_SOAK_DAYS";
    public const string RealSoakMinutes = "AIASSISTANT_REAL_SOAK_MINUTES";
    public const string MacHelperE2E = "AIASSISTANT_MAC_HELPER_E2E";
    public const string PortalE2E = "AIASSISTANT_PORTAL_E2E";
    public const string LinuxBackend = "AIASSISTANT_LINUX_BACKEND";
    public const string WaylandPipeWire = "AIASSISTANT_WAYLAND_PIPEWIRE";
    public const string TestSecretPrefix = "AIASSISTANT_TEST_SECRET_";
}
