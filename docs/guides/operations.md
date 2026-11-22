# 迁移与运维手册

> **状态**：living ｜ **最后更新**：2026-11-22 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：换机流程、维护命令、打包入口或环境前置要求变化时

换机接续、本机模型与语音配置、MCP 联调、维护命令、发布打包、浸泡与 Wayland 抓屏的操作口径。
参数与环境变量的权威清单见 [../reference/cli.md](../reference/cli.md) 与 [../reference/environment-variables.md](../reference/environment-variables.md)。



### 3.1 环境前置要求

1. **.NET SDK**：安装 `.NET 10.0 SDK`（LTS，推荐）或 `.NET 11.0+ SDK`（`global.json` 已配置 `rollForward: latestMajor`，并固定编译目标为 `net10.0`）。
2. **macOS 额外要求**（进入 S3 时）：Xcode Command Line Tools (`swift --version` >= 6.0)，用于编译 `native/AiAssistant.MacHelper`。
3. **Linux 额外要求**（进入 S3 时）：Wayland 会话（或 X11 回退环境）、`libsecret-1-0`、`xdg-desktop-portal`、`pipewire`。

### 3.2 换机后首条自检命令

拉取仓库后，在任意平台终端执行以下命令验证基线是否健康：

```bash
dotnet --info
dotnet restore AiAssistant.sln
dotnet build AiAssistant.sln -c Release
dotnet test AiAssistant.sln -c Release
```

### 3.3 启动桌面端壳程序

```bash
dotnet run --project src/AiAssistant.Host/AiAssistant.Host.csproj
```

### 3.4 模型端点与日报配置

优先使用 `models.json`（示例见 `samples/models.example.json`）。默认路径为数据目录下 `models.json`，也可用 `AIASSISTANT_MODELS_CONFIG` 指定。

```json
{
  "schemaVersion": 1,
  "policy": {
    "allowIntranet": true,
    "allowPublicCloud": false,
    "defaultTier": "LocalOffline",
    "allowLocalFallback": true,
    "requirePublicCloudConsent": true
  },
  "endpoints": [
    {
      "tier": "LocalOffline",
      "provider": "ollama",
      "baseUrl": "http://127.0.0.1:11434",
      "chatModel": "gemma4:12b",
      "embeddingModel": "embeddinggemma-2:740m"
    }
  ]
}
```

- 密钥字段使用 `apiKeySecret` / `clientCertificatePasswordSecret` 引用 `data/secrets/model-secrets.enc` 中的名称，或使用 `env:变量名`；明文密钥不得写入 `models.json`。
- 企业端点支持 `enterpriseCaPath`、`clientCertificatePath`、`clientCertificateKeyPath`、`proxyUrl`、`timeoutSeconds`。
- 公网档即使配置，也必须 `allowPublicCloud=true` 且运行时显式同意；Host 外层策略门禁会阻断绕过 UI 的调用。
- 未提供 `models.json` 时回退到环境变量配置：

```bash
AIASSISTANT_MODEL_PROVIDER=ollama
AIASSISTANT_MODEL_BASE_URL=http://127.0.0.1:11434
AIASSISTANT_CHAT_MODEL=gemma4:12b
AIASSISTANT_EMBEDDING_MODEL=embeddinggemma-2:740m
AIASSISTANT_REPORT_TIME=18:30
AIASSISTANT_REPORT_DISABLE_SCHEDULE=true
```

模型基准评测（不启动 UI 与采集）：

```bash
AiAssistant.Host --benchmark-model --output artifacts/benchmarks/model.md
```

- 固定三用例：中文摘要、语义标签、结构化 JSON；报告含成功率、JSON 合规率、平均耗时与失败详情。
- 模型不可用时仍写出报告并以退出码 `1` 结束，便于 CI 区分「未安装模型」与「崩溃」。
- 本机 loopback 端点会显式绕过系统代理环境变量，避免公司代理导致 Ollama 请求失败。

运维自检：

```bash
AiAssistant.Host --help
AiAssistant.Host --version
AiAssistant.Host --doctor
```

- `--doctor` 输出加密库健康/Schema/事件与媒体计数、模型端点能力（10 秒超时）、MCP 状态、`pdfFont`，macOS 额外输出 `macosHelper`（status/restarts/TCC 三项）与全部平台能力行；库不健康时退出码 1。

真机采集自检（用于 macOS 权限验收与无头采集排查）：

```bash
AIASSISTANT_DATA_DIRECTORY=/tmp/aia-capture-smoke AiAssistant.Host --capture-smoke 20
```

- 无头运行采集管线 N 秒（5–300，默认 20），输出 `events/media/healthy`，事件为 0 时退出码 1。
- macOS 首次运行会触发屏幕录制/麦克风授权弹窗；授权后重跑即可看到事件增长。若前台是密码管理器等黑名单应用，`events` 为 0 属正常过滤。

无头报告与证据问答：

```bash
# 任意区间报告（Markdown + SVG + PNG + PDF + stats JSON），不启动 UI
AiAssistant.Host --report-range --from 2026-11-16 --to 2026-11-22 --output artifacts/reports

# 基于近 7 天本地证据的问答，可追加 --output 导出 Markdown
AiAssistant.Host --ask "本周数据库迁移进展如何" --days 7
```

- 无证据时问答明确降级并返回退出码 1；报告在无事件时输出“没有可用的本地活动记录”。
- 控制台统一 UTF-8 输出，避免中文路径乱码。

模型端点密钥与能力探测：

```bash
# 从标准输入读取密钥（终端下隐藏输入），写入 AES-GCM 加密库
echo "sk-..." | AiAssistant.Host --import-model-secret corp-api-key

AiAssistant.Host --list-model-secrets
AiAssistant.Host --delete-model-secret corp-api-key --confirm-delete-secret
AiAssistant.Host --probe-model
```

- 密钥值只写不读；`models.json` 用 `apiKeySecret` 引用名称。
- 诊断页“模型端点密钥”卡片提供同样的保存/刷新/删除能力。
- `--probe-model` 输出各档位的 chat/stream/embed/json/tools 能力与诊断信息。

检索质量基准（合成语料，16 查询 / 48 事件）：

```bash
# 概念向量替身（离线、无模型依赖）
dotnet test tests/AiAssistant.Report.Tests -c Release --filter "FullyQualifiedName~S9RetrievalQuality"

# 真实 embedding（需要 Ollama + embeddinggemma-2:740m）
AiAssistant.Host --benchmark-retrieval --output artifacts/benchmarks/retrieval-real.md
```

- 对比「关键词-only」与「关键词 + 向量 RRF」，输出 Recall@K、命中率、MRR 与逐查询排名。
- `HybridSearchOptions.EnableVectorRecall=false` 可关闭向量召回，用于离线降级与基线对照。
- `AIASSISTANT_EMBEDDING_QUERY_PREFIX` 可覆盖查询指令前缀；embeddinggemma 默认使用官方检索指令。
- `AIASSISTANT_EMBEDDING_DOCUMENT_STYLE` 控制索引文本格式：`default`（应用 | 标题 | 正文）或 `title-text`（`title: … | text: …`，embeddinggemma 默认）。
- 更换 embedding 模型或文档格式后运行 `AiAssistant.Host --rebuild-vectors` 重建向量索引。

中文语音与 OCR 黄金集（S4-R，模型资产不入库）：

```bash
AiAssistant.Host --benchmark-asr --samples artifacts/golden/asr --output artifacts/benchmarks/asr.md
AiAssistant.Host --benchmark-ocr --samples artifacts/golden/ocr --output artifacts/benchmarks/ocr.md
AIASSISTANT_SHERPA_MODEL_DIR=artifacts/models/sherpa-kws \
  AiAssistant.Host --benchmark-kws --samples artifacts/golden/kws --output artifacts/benchmarks/kws.md
```

- 样本格式：ASR 为 `<name>.wav` + 同名 `.txt`（参考答案），任意采样率/声道会自动转 16 kHz 单声道；OCR 为 `<name>.png` + `.txt`。
- KWS 语料为 `positive/` 与 `negative/` 两个子目录；报告含唤醒率、误唤醒率（次/小时）与唤醒延迟中位数。
- 报告含逐样本 CER、编辑数/字数、耗时、识别结果、参考答案与错误；模型缺失时标记 `engine unavailable`（不计为 100% 错误）并退出码 1。
- OCR 命令在注册了 `IImageOcrRecognizer` 的平台可用（macOS Vision、Linux RapidOCR）。
- 语料覆盖与目录约定见 `docs/testing/golden-sets.md`；首轮合成语料基线见 `docs/testing/baselines/2026-11-21-macos-arm64.md`。

### 3.5 本地语音模型与命令
`AIASSISTANT_SHERPA_MODEL_DIR` 指向同一语音模型根目录，建议结构：

```text
sherpa/
├── kws/  encoder.onnx · decoder.onnx · joiner.onnx · tokens.txt · keywords.txt
└── tts/  model.onnx · tokens.txt · lexicon.txt
```

`keywords.txt` 使用 sherpa-onnx KWS 关键词格式，至少包含“你好时光”及备选词；阈值在真实环境实测后调整。语音默认关闭，由“语音”页显式启用；`AIASSISTANT_VOICE_DISABLE_CHIME=true` 可关闭唤醒提示音。语音问答只在“问一下 / 请问 / 查询 / 搜索”触发词后进入，回答与“检索与问答”面板共用同一证据用例。

语音反馈输出：Windows（WinMM/WASAPI）与 Linux（PulseAudio）直接在进程内播放；macOS 经 Swift Helper 的 `audio.play` 方法用 AVAudioEngine 渲染内存 PCM（不写临时文件、每次播放后释放设备，音频输出不需要 TCC 授权）。Helper 未就绪时能力降级为 `Degraded`，文字反馈不受影响。

命令音频默认不落盘：唤醒窗口内的音频只驻留内存，命令处理完即释放。需要回听时在“语音”页勾选“记录命令音频”，或用环境变量在启动时开启：

```bash
AIASSISTANT_VOICE_RECORD_COMMAND_AUDIO=true   # 默认 false
AIASSISTANT_VOICE_AUDIO_RETENTION_DAYS=7      # 1–365，默认 7
```

音频以 16 kHz 单声道 WAV 经 AES-GCM 写入媒体库，`voice_commands.audio_content_id` 记录引用；保留期到期后由 `MediaRetentionService` 安全擦除并把引用置空，语音日志的识别文本仍保留。当前无内置回听界面，需从加密媒体库导出后播放（见 `known-limitations.md`）。

语音识别默认走 sherpa-onnx Paraformer（低延迟，适合 8 秒命令窗口）。需要更高质量的长音频转写时切换到 Whisper.net 批处理引擎：

```bash
AIASSISTANT_ASR_ENGINE=whisper
AIASSISTANT_WHISPER_MODEL_PATH=/path/to/ggml-small.bin   # 省略时默认 <data>/models/whisper/ggml-small.bin
```

- 模型文件不入库；缺失时语音能力显示 `PermissionRequired` 并给出修复提示，采集与日报不受影响。
- 发布时 `AiAssistant.Host` 会按目标 RID 删除其它平台的 Whisper 原生库（约 16 MB），只保留当前平台。

Linux 图像 OCR 兜底（AT-SPI 无文本时按需触发，其它平台不受影响）：

```bash
AIASSISTANT_RAPIDOCR_MODEL_DIR=/path/to/rapidocr   # 省略时默认 <data>/models/rapidocr
```

模型目录需包含 `ch_PP-OCRv4_det_infer.onnx`、`ch_PP-OCRv4_rec_infer.onnx` 与 `ppocr_keys_v1.txt`。缺失时 `Ocr` 能力显示 `PermissionRequired`，文本提取回退到应用名/窗口标题，采集与日报不受影响。OCR 只引用 `Microsoft.ML.OnnxRuntime.Managed` 托管绑定，原生 ONNX Runtime 复用 sherpa-onnx 自带的 1.28.2；不要再引入 `Microsoft.ML.OnnxRuntime`（见 ADR-0007）。

### 3.6 只读本地 MCP（默认关闭）

本地 MCP 服务只监听 `127.0.0.1`，默认不绑定任何端口；启用与限流通过环境变量控制：

```bash
AIASSISTANT_MCP_ENABLED=true
AIASSISTANT_MCP_PORT=47831      # 端口占用时自动回退到临时端口
AIASSISTANT_MCP_RPM=60          # 每分钟请求上限
```

- 令牌由 32 字节随机数生成，以 AES-GCM 加密存放于 `<data>/security/local-mcp-token.enc`，包装密钥来自系统密钥库；在“本地诊断”页可显示或轮换令牌。
- 客户端配置示例（Claude Desktop / 任意 MCP 客户端）：

```json
{
  "mcpServers": {
    "aiassistant-local": {
      "url": "http://127.0.0.1:47831/mcp",
      "headers": { "Authorization": "Bearer <诊断页复制的令牌>" }
    }
  }
}
```

- 仅暴露四个只读工具：`search_events`、`activity_stats`、`list_reports`、`get_report`。不存在写入、采集、导出或删除工具；未授权、非 loopback、超限请求分别返回 401 / 直接断开 / 429。
- 工具返回结构化证据与 `mediaAvailable` 标记，从不返回截图或音频字节；媒体到期清理后该标记为 `false`。
- 协议兼容：JSON-RPC 2.0 over Streamable HTTP，支持 `application/json` 与单事件 `text/event-stream`、`Mcp-Session-Id`、协议版本协商（`2024-11-05` / `2025-03-26` / `2025-06-18`）与 `DELETE /mcp` 会话结束。
- 无头运行与令牌：`AiAssistant.Host --mcp-serve` 仅启动加密库与 MCP（不启动采集/UI）；`--mcp-token` 打印本地令牌供客户端粘贴。
- 一致性校验：`python tools/mcp/mcp-client.py --token <token> --check-unauthorized`（17 项）或追加 `--accept sse`（18 项）。

### 3.7 维护命令（备份 / 恢复 / 清空）

```bash
# 加密备份（含完整 Schema）
AiAssistant.Host --backup /path/to/backup.enc

# 恢复（必须显式确认，会覆盖当前数据库）
AiAssistant.Host --restore /path/to/backup.enc --confirm-restore

# 清空采集数据（事件/媒体/日报/任务/语音日志/向量索引）
AiAssistant.Host --purge-data --confirm-purge
```

退出码：`0` 成功、`2` 启动或维护失败、`3` 参数错误或缺少确认开关。维护命令会先初始化加密 Schema，再执行操作，不会启动采集与 UI。

### 3.8 发布打包

```powershell
pwsh build/package/package-windows.ps1 -Version 0.1.0          # zip；-Msix 追加 MSIX
```
```bash
bash build/package/package-macos.sh --skip-sign                 # 布局/CI；正式发布去掉 --skip-sign
bash build/package/package-linux.sh                             # tar.gz；有 dpkg-deb 时追加 .deb
```

签名通过环境变量注入：`AIASSISTANT_SIGN_CERT_THUMBPRINT`（Windows）、`AIASSISTANT_CODESIGN_IDENTITY` + `AIASSISTANT_NOTARY_PROFILE`（macOS）。发布工作流见 `.github/workflows/release.yml`，详细说明见 `build/package/README.md` 与 `docs/release/`。

打包脚本会在输出目录生成 `SHA256SUMS`；离线 SBOM 生成器不依赖外部工具：

```bash
dotnet restore AiAssistant.sln
python build/sbom/generate-sbom.py --verify --check-manifest
# 输出 artifacts/sbom/bom.json（CycloneDX 1.5，含 SHA-512 与依赖图）
```

### 3.9 多日加速浸泡

```bash
# 默认 3 天（72 小时等效，含 8 小时休眠窗口），约 16 秒
dotnet test tests/AiAssistant.Platform.Tests -c Release --filter "FullyQualifiedName~MultiDayAcceleratedSoak"

# 扩展到 14 天等效
AIASSISTANT_SOAK_DAYS=14 dotnet test tests/AiAssistant.Platform.Tests -c Release \
  --filter "FullyQualifiedName~MultiDayAcceleratedSoak"
```

报告写入 `artifacts/soak/soak-<days>d.md`，包含控制事件、黑名单拦截、队列峰值、活跃日期、完整性、保留清理与泄露检查结论。CI 会随测试上传该报告。

### 3.10 Wayland 连续抓屏（PipeWire）

授权 xdg-desktop-portal 后，抓屏优先消费 `OpenPipeWireRemote` 返回的连续帧流；失败时自动回退到 portal 按需截图。

```bash
# 关闭连续帧流，强制按需截图
AIASSISTANT_WAYLAND_PIPEWIRE=false

# 真机端到端验证（会弹出 GNOME 共享屏幕授权；需真实 Wayland 会话）
AIASSISTANT_PORTAL_E2E=1 dotnet test tests/AiAssistant.Platform.Tests -c Release \
  --filter "FullyQualifiedName~LiveWaylandScreenCast"
```

- 帧流为私有 `pw_stream`（方向 INPUT），按最小采样间隔 1 秒节流，只保留最新一帧；4K 屏幕不会持续拷贝内存。
- 协商格式支持 BGRx/BGRA/RGBA/RGBx，其它格式（如 YUV）会降级为截图回退而不是保存错误像素。
- 已在 Ubuntu 26.04 + GNOME Wayland（2560×1440）实测通过；KDE/KWin 与 X11 会话仍需人工验收。

---

