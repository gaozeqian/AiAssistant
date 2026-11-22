# AI 个人活动追踪助手 · 实施进度与跨平台交接记录 (PROGRESS)

> **用途**：本文件随 Git 仓库同步，作为跨电脑、跨操作系统（Windows / macOS / Linux）接续开发的唯一权威状态源。
> 每次完成子任务或阶段验收后必须同步更新本文件及 `IMPLEMENTATION.md` 第 10 节清单。

---

## 1. 当前总体状态快照

- **当前活跃阶段**：`S9 发布加固`：`win-x64` 已完成真实 UI 全流程与 90 分钟浸泡；剩余为真机签名/公证、管理员安装卸载、热插拔、模型资产与真实企业端点等门禁
- **最近更新时间**：`2026-11-22`
- **最近执行环境**：`win-x64` Windows 11（10.0.26300）+ `.NET SDK 10.0.402` + 局域网模型 `http://192.168.1.109:11434`（`gemma4:12b-mlx` / `embeddinggemma-2:740m`）；上一轮 `osx-arm64` macOS 26.7.3 + SDK 10.0.404；再上一轮 `linux-x64` Ubuntu 26.04 + GNOME Wayland
- **当前构建状态**：✅ `win-x64` 本轮 Release 0 警告 0 错误（Host/Desktop/Report.Tests 实建）；`osx-arm64` Swift Helper 历史结论见第 4 节日志
- **当前测试状态**：✅ 本轮 `win-x64`：真实窗口 `--ui-e2e` **17/17 连续 3 次通过（退出码 0）**，装入 KWS 唤醒词模型后 **17/17、0 跳过**（语音页可启用/关闭）；`S9RealTimeSoakTests` **90 分钟真实时钟浸泡通过**（5402.3s、2523 轮、控制 32/32、黑名单 84/84、4727 事件、2348 媒体、暂停窗口 0、完整性 ok）；`--benchmark-model` 100%/100%/783ms；定向用例 3/3 通过。⚠️ 本机 `dotnet test` 因 VSTest 宿主回环 socket 崩溃（`0xC0000005`，沙箱内外均复现）无法全量执行，改用进程内宿主加载同一测试程序集运行；历史全量结论仍为 `osx-arm64` 217/217
- **下一步待确认事项**：`S9-R4` 需先恢复 GitHub 账单/支出限额；`S9-R1`/`S3-R1` 需真实证书与 macOS Developer ID；`S9-R3` 需管理员会话实装 MSI 并做人工走查签字；`S3-R2` 需 KDE Wayland/X11 真机；`S4-R`/`S7-R1` 需外挂语音/OCR 模型与真人唤醒词正样本；`S9-R2` 仅剩显示器/麦克风热插拔。详见第 6 节

---

## 2. 阶段门禁总表（S0 – S9）

| 阶段 | 设计基线 | 状态 | 已验证平台 | 核心产出与门禁结论 |
|---|---|---|---|---|
| **S0 工程奠基** | P0 前置 | ✅ 已完成 | `win-x64` 实测；`osx-arm64` / `linux-x64` 交叉打包验证 | 19 个项目解决方案、CPM、10 份 ADR、CI 矩阵、6 条架构依赖测试通过 |
| **S1 核心与存储** | P0 | ✅ 已完成 | `win-x64` / `osx-arm64` 实测通过（`Storage.Tests` 17/17）；`linux-x64` 通过 | SQLite3MC 页级加密、DPAPI/Keychain/libsecret 密钥封装与轮换、AES-GCM 媒体库与引用计数、原子事件+媒体事务、加密备份恢复、`sqlite-vec` + SIMD 向量索引与重建 POC 全部通过 |
| **S2 Windows 最小闭环** | P0 | ✅ 已完成 | `win-x64` 采集、Headless UI、真实桌面强杀重启、24h 全天加速浸泡均通过 | 完成采集与有界管道、Avalonia MVVM 时间线、选中后解密预览、暂停/黑名单、托盘、自启开关、基础保留清理 `MediaRetentionService`、零敏感泄露结构化日志 `SanitizedStructuredLoggerProvider` 与导出预览；24h 浸泡验证控制事件 100% 零丢失 |
| **S3 三平台扩展** | P1 | 🔄 macOS 真机已验证 TCC 拒绝降级；TCC 允许流程、公证与 KDE/X11 矩阵待验收 | GitHub Actions 三平台构建/测试/打包通过；`osx-arm64` 真机 Helper 握手/心跳/降级实测；`linux-x64` GNOME Wayland 真机抓屏实测 | macOS Helper、Linux Wayland 默认与初始化失败 X11 回退、portal 加密令牌轮换、AT-SPI/EWMH、inotify/PulseAudio/XDG 均已接入；连续 PipeWire ScreenCast 帧流（§3.10）与 portal D-Bus 报文缺陷修复 |
| **S4 预处理与音频** | P1 | 🔄 代码与契约完成；模型资产评测遗留 | `win-x64` 流水线/AudioHub/WASAPI 实测；`linux-x64` 全量测试与发布（含 sherpa-onnx / Whisper 原生库）通过 | 可组合阶段、超时/失败隔离、脱敏、pHash 去重、单路 AudioHub、三平台音频工厂、sherpa-onnx ASR/VAD/说话人分离与缺失模型降级；Whisper.net 批处理 ASR 可选引擎；RapidOCR PP-OCRv4 Linux 图像 OCR 兜底（§3.5）；真实中文语音与 OCR 质量待遗留项验收 |
| **S5 本机模型与日报** | P2 | 🔄 代码与契约完成；真实模型评测遗留 | `win-x64` 网关/分段/调度/UI 实测；`linux-x64` / `osx-arm64` 发布通过 | Microsoft.Extensions.AI 网关、Ollama/LM Studio 兼容适配、脱敏/审计/超时装饰器、事件向量索引、分段+map-reduce+JSON 校验重试、版本化加密日报、单消费者三通道与日报/站会 UI；真实模型质量待遗留项评测 |
| **S6 内网与公网档** | P2.5 | 🔄 代码与契约完成；真实企业端点遗留 | `win-x64` 配置/策略/三闸/探测测试通过；两平台发布通过 | 版本化 `models.json`、加密端点密钥、mTLS/企业 CA/代理、Host 外层策略锁定、公网显式同意、三档路由与内网→本机回退、三闸脱敏、五能力探测缓存与诊断展示 |
| **S7 唤醒与基础命令** | P2.6 | 🔄 代码与契约完成；macOS 音频输出已真机验证；真实 KWS/TTS 黄金集遗留 | `osx-arm64` 真机 Helper 音频回放与 TCC 降级通过；`win-x64` 状态机/命令/日志/UI 测试；`linux-x64` 全量 151/151；三平台发布通过 | sherpa-onnx “你好时光” KWS、四态语音会话、8 秒命令窗口、误唤醒冷却、固定命令、敏感命令二次确认、TTS 反馈、加密 voice_commands 文本日志与状态栏/语音面板；命令音频 opt-in（§3.5）；macOS `audio.play`（AVAudioEngine 内存 PCM） |
| **S8 智能检索与对话** | P3 | 🔄 代码与契约完成；真实检索质量与 MCP 客户端联调遗留 | `win-x64` 混合检索/证据问答/区间报告/MCP 契约与 Headless UI 测试；两平台发布通过 | 时间过滤+关键词+向量 RRF 混合检索与 `[E#]` 证据引用、无证据/无引用/媒体清理三类降级、周报与任意区间 Markdown+stats 导出、只读 loopback MCP（加密令牌/限流/四个只读工具）、检索问答面板与证据回跳、语音问答复用同一用例 |
| **S9 发布加固** | 全阶段 | 🔄 代码与工具链完成；`win-x64` 90 分钟浸泡与真实 UI 全流程通过；真机签名/热插拔/安装卸载遗留 | `win-x64` 打包与维护命令实测、真实窗口 UI E2E 17/17×3、90 分钟真实浸泡通过；`linux-x64` / `osx-arm64` 包布局验证 | 跨平台打包脚本（Windows zip+MSIX 模板+签名、macOS app+codesign/notarize、Linux tar.gz+deb）、应用图标、发布工作流、数据删除服务与 CLI（backup/restore/purge）、v1→v2 迁移与密钥丢失故障注入、数据与导出页面、键盘快捷键/无障碍名称/权限引导、隐私/限制/性能/升级卸载/回滚/发布检查表文档 |

---

## 3. 跨电脑 / 跨平台迁移接续指南

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

音频以 16 kHz 单声道 WAV 经 AES-GCM 写入媒体库，`voice_commands.audio_content_id` 记录引用；保留期到期后由 `MediaRetentionService` 安全擦除并把引用置空，语音日志的识别文本仍保留。当前无内置回听界面，需从加密媒体库导出后播放（见 `KNOWN-LIMITATIONS.md`）。

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

## 4. 实施日志（按时间顺序追加）

- **2026-11-20 [win-x64]**：初始化 `PROGRESS.md`，核对本机 SDK（`.NET SDK 11.0.100` + `.NET Runtime 10.0.15`）及 NuGet 核心包可用版本（`Avalonia 12.1.6`、`Avalonia.Wayland 12.1.6`、`Microsoft.Data.Sqlite.Core 10.0.15`、`SQLite3MC.PCLRaw.bundle 2.7.1`、`HiraokaHyperTools.sqlite-vec 0.1.9`、`Microsoft.Extensions.AI.Abstractions 10.11.0`），启动 S0 工程奠基。
- **2026-11-20 [win-x64] [S0 完成]**：完成 `AiAssistant.sln`（14 个 `src` 模块 + 5 个 `tests` 项目 + `native/AiAssistant.MacHelper` Swift 骨架）、`Directory.Build.props`、`Directory.Packages.props`、10 份 ADR（`docs/adr/0001..0010`）、GitHub Actions 三平台 CI 与 SBOM 工作流、6 条架构门禁测试；验证 `win-x64` / `osx-arm64` / `linux-x64` 发布目录均正确携带 `sqlite3mc` 与 `vec0` 原生库。进入 S1。
- **2026-11-20 [win-x64] [S1 契约完成]**：完成 `AiAssistant.Core` 零依赖领域模型（`ActivityEvent`、`OccurredTimestamp` 跨时区归档时间模型、`CapabilityState`、`PerceptualHash`、`ContentRef`、`ReportJob` 退避恢复状态机、`SchemaVersion`、领域异常）及 `AiAssistant.Application` 全部核心端口与基础用例（`IClock`、`ICaptureSource`、`IEventRepository`、`IMediaStore`、`IKeyStore`、`IVectorIndex`、`IReportJobQueue`、`IAudioHub`、`ITextExtractor`、`PrivacyBlacklistPolicy`），15/15 单元与架构测试通过。
- **2026-11-20 [win-x64] [S1 完成 & POC 通过]**：完成 `AiAssistant.Storage`（`EncryptedSqliteConnectionFactory`、`SchemaMigrator`、`OsProtectedKeyStore`、`AesGcmMediaStore`、`SqliteEventRepository`、`SqliteReportJobQueue`、`SqliteDatabaseMaintenanceService`、`HybridSqliteVectorIndex`）。验证磁盘原始字节无 SQLite 明文头且无明文正文、错误密钥抛 `StorageAuthenticationException`、篡改媒体抛 `StorageIntegrityException`、事务中断零残留、加密备份与恢复、禁止旧代码写高版本 Schema、`sqlite-vec` 联合加载与模型维度切换重建。21/21 测试通过。进入 S2。
- **2026-11-20 [win-x64] [S2 采集与有界管道完成]**：完成 `AiAssistant.Platform.Windows`（前台窗口焦点监听 `WindowsWindowActivitySource`、GDI 缩放截屏+托管 PNG 编码+pHash `WindowsScreenCapture`、200KB 白名单目录监听 `WhitelistedFileActivitySource`、排除密码框的 UI 树优先+OCR 兜底 `WindowsTextExtractor`、HKCU 可选自启 `WindowsAutoStartService`）、三平台 `ICapabilityProvider` 以及 `AiAssistant.Capture`（高优先级控制通道 + 有界数据通道 `BoundedCaptureCoordinator`：暂停期间零落盘且恢复不补采、黑名单过滤、pHash 近重复帧合并、过载有界淘汰）。27/27 测试通过，按步分拆提交 Git 并暂停等待用户确认下一步。
- **2026-11-20 [win-x64] [S2 UI 与故障闭环完成]**：实现 `AiAssistant.Desktop` MVVM 时间线（活动日期、关键字、事件详情）、仅在选中后调用 `TimelineUseCase` 解密并内存解码缩略图、旧请求取消与 Bitmap 释放；完成暂停/恢复、黑名单、立即快照、自启开关、系统托盘、本地能力/任务诊断和关闭隐藏。新增 `AppRuntime` Composition Root，接通加密库迁移、运行中任务恢复、采集启动与退出释放，支持 `AIASSISTANT_DATA_DIRECTORY` 隔离和 `--startup-check`。新增 5 个 Headless Desktop 测试与异常重启一致性测试；真实桌面进程运行 5 秒后强制终止，再启动加密库成功。全量 Release 构建 0 警告 0 错误，33/33 测试通过。S2 保留全天浸泡最终门禁。
- **2026-11-20 [win-x64] [S2 最终门禁验收完成]**：补齐基础保留策略协调器 `MediaRetentionService`（启动清理 + 周期擦除到期 `.enc` 媒体并保留结构化历史事件）与隐私脱敏结构化日志 `SanitizedStructuredLoggerProvider`（有界 JSONL 滚动日志、强制拦截敏感键名/密钥/完整路径/URL 查询串，并在诊断页提供导出前透明预览）。修复软配额满时 `OpenConnectionForPathAsync` 误阻断查询/清理/控制审计的缺陷，并加固 `BoundedCaptureCoordinator` 退出控制通道排空与重试保证。通过跨午夜 24 小时加速全天浸泡验收测试（过载洪峰与配额压力下 48/48 控制事件零丢失、队列严格受限、日志与导出预览零正文/零密钥泄露）。全仓 Release 构建 0 警告 0 错误，37/37 测试通过。S2 正式收官，准备进入 S3。
- **2026-11-20 [win-x64] [S3.1 macOS IPC 与 Helper 完成]**：实现 `MacHelperProtocol` v1（4 字节大端长度 + 有界 JSON 帧）、`UnixDomainSocketMacHelperClient`（并发请求关联、写锁、超时与断线处理）、`MacHelperSupervisor`（短 `sun_path`、一次性 256-bit 会话 Token、握手、心跳、指数退避重启、协议不兼容终止重启）。完成 ScreenCaptureKit、NSWorkspace/AX（排除 `AXSecureTextField`）、Vision OCR、原生 FSEvents、AVAudioEngine 16kHz 单声道、LaunchAgent 适配器与动态能力矩阵并接入 Host。补充 `Info.plist`、entitlements、CI Swift 编译/冒烟/打包断言；`osx-arm64` 交叉发布成功，Swift 6.4.2 对清单和全部源码语法解析通过。新增 6 项真实 AF_UNIX IPC 与故障注入契约测试，全量 Release 构建 0 警告 0 错误、43/43 测试通过。macOS 原生编译、TCC 权限与签名公证需真机或 CI 最终复核。
- **2026-11-20 [win-x64] [S3.2 Linux 平台扩展完成]**：实现 `LinuxDisplayBackendState` 与仅在 Avalonia `SetupWithLifetime` 初始化阶段捕获的 Wayland→X11 回退；基于 `Tmds.DBus.Protocol 0.95.2` 实现 portal `CreateSession → SelectSources → Start → OpenPipeWireRemote`、请求前信号订阅、单次 `restore_token` AES-GCM 加密与逐次轮换，授权失败仅降级且用户取消不重复弹窗。实现 AT-SPI2（窗口焦点、文本接口、密码角色排除）、X11 EWMH/XGetImage、托管 PNG 编解码与像素转换、inotify 白名单文件、惰性 PulseAudio 和 XDG 自启；全部接入 Host 与动态能力矩阵。Wayland 不做周期弹窗，手动截图以 portal Screenshot 回退并标记 Degraded，连续 PipeWire 像素流待真实桌面复核。新增 10 项 Linux 专项契约测试，全仓 Release 0 警告 0 错误、53/53 测试通过，`linux-x64` 发布与 `Tmds.DBus.Protocol.dll` 打包验证通过。
- **2026-11-20 [GitHub Actions] [S3 首轮 CI 修复]**：运行 `44696472530` 中 Windows 与 SBOM/许可证作业通过，macOS/Linux 测试暴露跨平台路径脱敏缺陷（Unix 宿主不能用 `Path.GetFileName` 解析反斜杠路径），macOS 另有 IPC 超时测试受监督器时序影响。已将脱敏改为同时识别 `/` 与 `\\` 的主机无关实现，并将超时/断线测试收窄为直接真实 AF_UNIX 传输测试；本地 Release 53/53 测试通过。复验运行 `44701041589` 中三平台各 53/53 测试、全部发布、Swift Helper 编译/冒烟/打包、Linux Portal 打包及 SBOM/许可证检查全部通过。
- **2026-11-20 [GitHub Actions] [CI 测试稳定性修复]**：后续文档提交的 Windows 作业偶发暴露 `BoundedCaptureCoordinatorTests` 断言竞态：测试仅等待首个事件落库便读取重复合并/黑名单计数。生产逻辑无回归；测试改为同时等待三项指标并在 CI 负载下延长有界等待。专项与全量 Release 53/53 复跑通过。修复提交 `4df8f4c` 的运行 `44703620747` 中三平台各 53/53 测试、发布、Swift Helper 编译/冒烟/打包、Linux Portal 打包与 SBOM/许可证检查全部通过。
- **2026-11-20 [win-x64] [S4 预处理与统一音频入口完成]**：实现 `ProcessingPipeline`（阶段版本/资源声明、单阶段超时、取消与异常隔离、失败仅降级元数据）、`PerceptualDeduplicationStage`、`TextExtractionStage`、`SensitiveDataRedactor`/`RedactionStage`、`AudioTranscriptionStage` 与 `SpeakerDiarizationStage`，并接入 `BoundedCaptureCoordinator`；预处理失败仍持久化原始事件。实现单路 `AudioHub`（多订阅有界广播、2 秒纯内存预滚、最后订阅退出关闭设备、再次订阅重开）。接入 `org.k2fsa.sherpa.onnx 2.0.2` 的 Paraformer 中文 ASR、Silero VAD、pyannote 说话人分离及模型缺失明确降级；新增 NAudio 3.3 WASAPI 工厂、macOS AVAudioEngine 工厂和 Linux PulseAudio 工厂。全仓 Release 0 警告 0 错误、62/62 测试通过，Host 启动自检与 `linux-x64`/`osx-arm64` 发布（含 sherpa-onnx 原生库）通过。真实模型质量、Linux RapidOCR 与 Whisper 批处理保留在第 5 节遗留项。
- **2026-11-20 [win-x64] [S5 本机模型与日报完成]**：实现 `HttpModelBackend`（Ollama 与 OpenAI 兼容端点，标准 `IChatClient`/`IEmbeddingGenerator`）、仅 loopback 的 `LocalModelGateway`、超时/脱敏/审计装饰器与无正文模型审计；模型标签全部由环境配置。实现 `EventEmbeddingIndexer`（本地 embedding 默认，版本化写入与重建）和固定中文基准集。实现活动分段、map 摘要、reduce 结构化 JSON、Schema 校验与两次重试、Markdown/站会输出及加密版本化 `daily_reports` 仓储。实现单消费者 `ReportJobWorker`、手动/启动补偿/每日定时三通道、幂等与失败保留；新增日报/站会面板、强制重生成与 Markdown 导出。全仓 Release 0 警告 0 错误、73/73 测试通过，Host 启动自检与两平台发布通过。首轮 CI `44710041839` 的 Linux/macOS/SBOM 通过，Windows 暴露 macOS IPC 启动瞬时断言竞态；改为等待监督器最终 Healthy 后本地全量复跑通过，由修复提交触发三平台复验。真实模型质量评测与调度浸泡保留遗留项。
- **2026-11-20 [win-x64] [S6 企业策略与三档模型完成]**：实现版本化 `models.json` 与 `models.example.json`，校验 schema/重复档位/远程本地端点；端点 API Key 与证书密码通过 AES-GCM 加密密钥库或 `env:` 引用，明文不入配置。实现每端点独立 HttpClient（代理、PFX/PEM mTLS、企业 CA 自定义信任、超时）。Host 改为多端点装配，`PolicyEnforcedModelGateway` 在应用层外强制档位策略，公网需显式 `PublicCloudConsentGate` 同意；`TierRoutingModelGateway` 支持内网故障回退本机、本机不可用明确失败。新增最小化→脱敏→残留检测/输出清洗三闸与敏感残留阻断；五能力（chat/streaming/embeddings/structured/tool）探测缓存 24 小时并接入诊断。全仓 Release 0 警告 0 错误、80/80 测试通过，Host 无配置安全回退启动与两平台发布通过。真实企业 mTLS/CA/代理和公网端点验证保留遗留项。
- **2026-11-20 [win-x64] [S7 本地唤醒与基础命令完成]**：接入 `org.k2fsa.sherpa.onnx` KWS 与 VITS TTS，新增 `VoiceSessionController` 四态状态机（休眠/聆听/处理/反馈）、8 秒命令窗口、超时/取消、误唤醒冷却与同一 AudioHub 单路采集；固定命令映射到既有日报、采集和导航用例，退出应用需二次确认。实现加密 `voice_commands` 文本日志（Schema v2 新增可选 audio_content_id）与命令音频默认不落盘；新增 Windows/Linux 反馈播放，macOS 音频输出明确降级。新增“语音”面板、状态栏状态与语音开关。全仓 Release 0 警告 0 错误、87/87 测试通过，Host 启动与两平台发布通过。真实 KWS/TTS 评测和命令音频 opt-in 保留遗留项。
- **2026-11-20 [win-x64] [S8 智能检索与完整对话完成]**：新增 `AiAssistant.LocalApi` 项目并把检索/问答/区间报告提升为 Application 端口（`IEvidenceSearch`、`IEvidenceQuestionAnswerer`、`IRangeReportService`、`ILocalMcpControl`），Desktop 只依赖 Application。实现 `HybridSearchService`（时间过滤 + 关键词 + 向量 RRF 融合、统一 `[E#]` 证据、向量失败仅降级关键词）与 `EvidenceQuestionAnswerUseCase`（强制引用校验，无引用走抽取式降级，无证据返回 `no-evidence` 且不调用模型）。实现 `ActivityRangeReportService` 与 `RangeReportUseCase`：周一为界的周报、任意区间每日事件数/活跃分钟/主要应用、Markdown + stats JSON 双文件导出、模型不可用确定性统计降级。实现只读 loopback MCP：`TcpListener` 仅绑 `127.0.0.1`、AES-GCM 加密令牌（可轮换）、滑动窗口限流、JSON-RPC `initialize/ping/tools/list/tools/call`、四个只读工具、未知或写入工具返回 -32602；Host 生命周期接入且默认关闭。新增“检索与问答”面板（检索/提问/导出、证据卡片一键定位时间线、周报柱状与导出）、“本地诊断”MCP 令牌显示/轮换，语音“问一下…”复用同一证据用例。全仓 Release 0 警告 0 错误、106/106 测试通过（新增 16 项检索/报告/MCP 契约测试与 2 项 UI/语音测试），两平台发布通过。真实 embedding 质量与真实 MCP 客户端联调保留遗留项。
- **2026-11-20 [win-x64] [S9 发布加固工具链完成]**：新增 `build/package/` 跨平台打包脚本与图标生成器（纯 Python 生成 PNG/ICO/MSIX 资源）：Windows 自包含 zip + 可选 Authenticode/MSIX、macOS `.app` + codesign/notarytool/stapler、Linux tar.gz + `.deb`；新增 `.github/workflows/release.yml` 签名发布流水线与 CI 打包作业。新增 `IDataDeletionService`/`DataDeletionService`（按日期或全量删除事件、媒体、日报、任务、语音日志、向量索引，WAL checkpoint）与“数据与导出”页面（二次确认删除）、Host 维护命令 `--backup` / `--restore --confirm-restore` / `--purge-data --confirm-purge`（实测备份 84KB、恢复、清空、清空后启动均成功）。新增 v1→v2 迁移保留数据、按日期删除、全量删除后可用、密钥丢失不损坏数据库四项门禁测试；修复报告后台任务测试的时段依赖缺陷（固定时钟）。补充键盘快捷键（Ctrl+D1…D7、Ctrl+F、F5）、关键控件无障碍名称、诊断页权限引导与确认导出。新增 `docs/release/` 隐私说明、已知限制、性能报告（启动 0.82s、包体积 83MB/58MB）、升级卸载、回滚手册与发布检查表。全仓 Release 0 警告 0 错误、112/112 测试通过；Windows zip、Linux tar.gz、macOS app 布局实测生成。真实签名公证、多日浸泡与人工无障碍验收保留遗留项。
- **2026-11-20 [win-x64] [S9 供应链收尾]**：新增离线 CycloneDX 1.5 SBOM 生成器 `build/sbom/generate-sbom.py`（读取 `project.assets.json`，输出 SHA-512 哈希、purl 与依赖图，支持 `--verify` 与 `--check-manifest`），实测 103 个组件、104 条依赖边、校验通过；依赖清单补齐全部 28 个集中管理包。打包脚本自动生成 `SHA256SUMS` 并实测 `sha256sum -c` 通过；CI SBOM 作业改为离线生成（不再安装外部工具），Release 工作流合并产物后统一生成校验和并附 `bom.json`。新增 `docs/release/RELEASE-NOTES-TEMPLATE.md`。`S9-R4` 仅剩 CI 实际运行与 Release 附件待验证。
- **2026-11-21 [win-x64] [S9 三平台安装包与自签名交付]**：工作流已触发（run `44844933808`），Windows/macOS/Linux 三个 job 均在 5 秒内失败且无日志，Create Release 被跳过；判定为 GitHub Actions 账单/配额仍未恢复，未生成 Release。
- **2026-11-21 [win-x64] [S9 三平台安装包与自签名交付]**：按用户选择改为 MSI + PKG + deb。新增 WiX 5 MSI 工程（`Package.msi.wxs`、`package-windows-msi.ps1`，安装时以提权 custom action 导入 `.cer` 到本机 Root/TrustedPeople）；macOS 脚本改为生成 PKG，`postinstall` 以 root 信任证书；Linux 增加无 dpkg-deb 的 Python ar 构建器并修正 deb 内可执行位。本地 MSI 68.1MB 签名 Valid，deb 57.6MB 结构与权限校验通过。发布工作流改为 GitHub Actions 生成三平台自签名安装包并创建 draft Release；本地不再上传产物，之前误建的草稿已删除。
- **2026-11-21 [win-x64] [S9 Windows 打包复验]**：用 `package-windows.ps1 -Version 0.1.0 -Msix` 重新产出 `AiAssistant-0.1.0-win-x64.zip`（83.6 MB，311 文件，含 18 个 PDB）与未签名 MSIX（86.3 MB），生成 `SHA256SUMS`。解压 zip 后 `--version` 显示 `0.1.0+eeb91ed`、`--smoke-check` OK、`--startup-check` OK（恢复 1 个任务）；`AiAssistant.Host.exe` 直接运行 `--smoke-check`/`--startup-check` 退出码均为 0。MSIX 待真实证书签名；自签名安装/卸载链路已在 S9.21 验证。
- **2026-11-21 [win-x64] [S9 真实时间浸泡测试]**：新增 `S9RealTimeSoakTests`，按 `AIASSISTANT_REAL_SOAK_MINUTES`（1–120）用真实时钟持续提交屏幕/窗口样本、每 5 分钟暂停/恢复、周期性黑名单样本，校验控制事件、队列上限、黑名单计数、暂停窗口零事件、完整性。5 分钟冒烟通过：142 轮、284 事件、142 加密媒体、黑名单 5/5、队列峰值 2/12、完整性 ok。60 分钟运行完成：3600.3s、1705 轮、3191 事件、1585 媒体、控制事件 22/22、队列峰值 2/12、暂停窗口零事件、完整性 ok；黑名单 53 次均为非暂停期提交（4 次暂停期提交被正确忽略），修正测试期望后 8 分钟复验 7/7 通过。
- **2026-11-20 [win-x64] [S9 本地企业 mTLS/代理模拟]**：本机搭建 HTTPS 双向 TLS（自签 CA、服务端证书、客户端 PFX）与 CONNECT 代理，`models.json` 配置 CorporateIntranet + `enterpriseCaPath` + `clientCertificatePath` + `proxyUrl`。`--probe-model` 显示 intranet chat/stream/embed 全 True，服务端日志确认 `CN=AiAssistant Test Client`；移除客户端证书后 intranet 正确变为不可用。期间修复两个真实缺陷：企业 CA 用 `CreateFromPemFile` 单参数加载会误走私钥解析（改用 `X509CertificateLoader.LoadCertificateFromFile`）；`--benchmark-model` 始终请求 LocalOffline 却标注默认档模型（改为按策略默认档位请求并记录实际响应模型）。新增 2 项测试，全仓 146/146。
- **2026-11-20 [win-x64] [S9 自动化键盘与无障碍测试]**：7 个标签页补齐 `AutomationProperties.Name` 与 `Ctrl+1…7` 加速键，`TabControl` 标记为「主导航」；新增 3 项 Headless 测试：标签页名称/加速键、关键控件无障碍名称（时间线/检索/诊断）、真实 `KeyPress` 验证 Ctrl+D2/Ctrl+D7/Ctrl+F 切页。全仓 144/144 测试通过。
- **2026-11-20 [win-x64] [S9 MCP Inspector 联调]**：用 `@modelcontextprotocol/inspector@2.16.0 --cli` 对 `--mcp-serve` 实例完成真实客户端联调：initialize 返回 `2025-03-26`/`aiassistant-local`；tools/list 恰好 4 个只读工具；activity_stats/search_events 返回 text 内容；无令牌退出码 3（auth_required）；调用 delete_events 退出码 5（tool_not_found）。新增 `build/mcp/inspector-smoke.ps1` 一键冒烟（固定 Inspector 版本、UTF-8 输出、键值参数）与 `build/mcp/README.md`。
- **2026-11-20 [win-x64] [S9 MSIX 自签名演练与数据保留修复]**：用 SDK 10.0.22621 的 `signtool`/`makeappx` + 自签名证书完成 MSIX 打包、签名校验（`Get-AuthenticodeSignature=Valid`）与松散注册启动；发现 MSIX AppData 虚拟化会把数据写入包容器并在卸载时删除，已在清单中声明 `unvirtualizedResources` 并关闭 `FileSystemWriteVirtualization`/`RegistryWriteVirtualization`。复测：数据写入真实 `%LOCALAPPDATA%\AiAssistant`，卸载后数据与密钥保留。非管理员无法导入机器级根证书，签名安装被拒（0x800B0109），改用 `Add-AppxPackage -Register` 完成布局/启动/卸载演练；测试证书已从用户存储清理。打包脚本新增 `-SkipTimestamp` 与 `-VerifyRootPath` 供自签名演练。
- **2026-11-20 [win-x64] [S9 PDF 字体自动发现]**：`PdfSharpReportRenderer` 在显式候选缺失时扫描系统字体目录（Linux/macOS/Windows），用 SkiaSharp 检查 `时/报` 字形覆盖，自动选择 TrueType CJK 字体；`--doctor` 新增 `pdfFont` 行。实测 Windows 选中 `simhei.ttf`，无需配置即可生成可检索 PDF。
- **2026-11-20 [win-x64] [S9 可检索 PDF]**：新增 `PdfSharpReportRenderer`，用 PDFsharp 6.1.1 + 系统 TrueType 中文字体（Windows `simhei.ttf`/`Deng.ttf`，macOS `Arial Unicode.ttf`，Linux Noto/AR PL 候选，可用 `AIASSISTANT_PDF_FONT_PATH` 覆盖）渲染可选中/检索的 PDF，并使用 `PdfFontEmbedding.TryComputeSubset` 只嵌入所用字形（约 30KB，字体子集约 25KB）；无合适字体时自动回退 Skia 矢量轮廓 PDF。pypdf 实测可提取标题、日期、事件数、分钟数与页脚。新增 `/ToUnicode` 断言；全仓 141/141 测试通过。
- **2026-11-20 [win-x64] [S9 周报 PNG/PDF 导出]**：新增 `AiAssistant.Export` 项目与 `IReportRasterizer` 端口，基于 Avalonia 已携带的 SkiaSharp 3.119.4 渲染 PNG（2 倍缩放）与 PDF；PDF 将文字转为字形轮廓再重解析路径，避免 CJK 字体整包嵌入（12MB → 约 35KB）。区间导出现在同时生成 Markdown / stats JSON / SVG / PNG / PDF，桌面与 `--report-range` 均生效。新增 4 项渲染测试（PNG 可解码尺寸、PDF 头尾与体积、空报告不崩溃）。全仓 141/141 测试通过。
- **2026-11-20 [win-x64] [S9 无头报告与证据问答命令]**：新增 `--report-range --from/--to [--output]`（生成 Markdown/SVG/stats 三件套）与 `--ask "<question>" [--days] [--output]`（复用证据问答用例，无证据退出码 1）；控制台统一 UTF-8。实测空库区间报告生成三文件、无证据问答输出明确降级；无效日期返回 3。全仓 137/137 测试通过。
- **2026-11-20 [win-x64] [S9 运维自检命令]**：新增 `--help`/`-h`、`--version` 与 `--doctor`；`--doctor` 组合加密库健康检查、模型能力探测（10s 超时）与 MCP 状态，库不健康返回 1。实测输出 `storage healthy schema=v2`、`models: probe timed out`（12B 模型首次探测超时，命令仍正常结束）与 `mcp disabled`。全仓 137/137 测试通过。
- **2026-11-20 [win-x64] [S9 14 天加速浸泡通过]**：`AIASSISTANT_SOAK_DAYS=14` 实测 73.8 秒完成 336 小时等效：674/674 控制事件零丢失、672 条黑名单拦截、队列峰值 12/12、1342 条事件、336 个媒体、8 小时休眠窗口零事件、完整性 `ok`、保留清理 336/336，日志/导出零泄露。报告 `artifacts/soak/soak-14d.md`。真实时间多日浸泡与设备热插拔仍为 `S9-R2` 遗留项。
- **2026-11-20 [win-x64] [S9 周报 SVG 图形化导出]**：新增 `RangeReportSvgRenderer`（纯 `StringBuilder`，无图像库依赖），输出 960×420 的每日柱状图、总量/模型副标题与脱敏页脚；`RangeReportUseCase.ExportAsync` 现在同时写出 Markdown、stats JSON 与 SVG 三个文件，检索页导出状态同步提示。新增 SVG 转义与逐日渲染测试，全仓 137/137 通过。
- **2026-11-20 [win-x64] [S9 模型密钥导入与端点探测]**：新增 `IModelSecretAdmin` 端口与 `ModelSecretAdmin`，`ProtectedModelSecretStore` 支持只列名称、删除返回是否命中；新增 `--import-model-secret`（标准输入读取、终端隐藏输入）、`--list-model-secrets`、`--delete-model-secret --confirm-delete-secret` 与 `--probe-model`；诊断页新增“模型端点密钥”卡片（名称/密码框/保存/刷新/删除）。端到端实测导入、列出、未确认拒绝删除、删除与空列表均正确；`--probe-model` 输出 gemma4:12b 的 chat/stream/embed 能力。新增加密存储与诊断页交互测试，全仓 136/136 通过。
- **2026-11-20 [win-x64] [S9 默认对话模型切换与旧模型配置清理]**：将 `ModelGatewayOptions`、`ModelEndpointDocument` 与 `models.example.json` 的默认对话模型切换为 `gemma4:12b`；移除旧检索指令常量、`EmbeddingProfile` 的旧模型分支，以及代码/测试/文档/示例中的全部旧模型引用。默认配置实测 `--benchmark-model` 识别 `gemma4:12b`，成功率与 JSON 合规率 100%。全仓 134/134 测试通过。
- **2026-11-20 [win-x64] [S9 默认 embedding 切换为 embeddinggemma]**：将 `ModelGatewayOptions`、`ModelEndpointDocument` 与 `models.example.json` 的本机默认 embedding 从旧默认 embedding 切换为 `embeddinggemma-2:740m`；未显式设置 `AIASSISTANT_EMBEDDING_MODEL` 时默认走 gemma（768 维、官方查询前缀、`title/text` 文档格式）。实测默认配置 `--benchmark-retrieval` 输出 95.8%/MRR 1.000。已有数据需运行 `--rebuild-vectors` 重建索引。全仓 135/135 测试通过。
- **2026-11-20 [win-x64] [S9 embeddinggemma 评测与生产接入]**：新增 `EmbeddingProfile` 统一解析模型相关默认值：embeddinggemma 使用 `task: search result | query:` 查询前缀与 `title: … | text: …` 文档格式。新增 `AIASSISTANT_EMBEDDING_DOCUMENT_STYLE` 与 `--rebuild-vectors` 命令，索引版本随文档格式变为 `1.1-title-text`。本机 embeddinggemma-2:740m（768 维）实测 Recall@8 **95.8%**、MRR **1.000**、热态约 19ms（三次中两次 95.8%，一次 93.8%）。全仓 134/134 测试通过。
- **2026-11-20 [win-x64] [S9 真实模型基准评测]**：用本机 Ollama `gemma4:12b` 运行 `--benchmark-model`：首次中文摘要 7.4s（含模型加载），热态语义标签 4.6s、结构化 JSON 1.8s；成功率与 JSON 合规率均为 100%。将基准 JSON 校验对齐生产解析（容忍 ```json 代码块与前后说明文字），避免把可用输出误判为不合规；基准假实现改为返回带代码块的 JSON 以覆盖该路径。
- **2026-11-20 [win-x64] [S9 真实 embedding 检索评测]**：新增 `AiAssistant.Host --benchmark-retrieval`，用真实配置的 embedding 端点对合成语料建索引并对比关键词/混合检索；新增 `HybridSearchOptions.EmbeddingQueryPrefix`（可用 `AIASSISTANT_EMBEDDING_QUERY_PREFIX` 覆盖）。本机真实 embedding 实测：关键词 Recall@8 50%/MRR 0.5；混合检索显著优于关键词基线，热态平均约 17 ms（首次含模型加载约 6.4 s）。同义词「同行评审」「上线流水线」仍有漂移，记录为后续调参样本。全仓 129/129 测试通过。
- **2026-11-20 [win-x64] [S9 检索质量基准]**：新增 `RetrievalBenchmarkEvaluator`（Recall@K、命中率、MRR、逐查询排名与对比报告）与 `HybridSearchOptions.EnableVectorRecall` 开关；新增合成语料基准测试（48 条事件 / 16 查询，8 个主题各含精确词与同义词查询）。实测关键词-only Recall@8 50%、MRR 0.500；混合 RRF Recall@8 100%、MRR 1.000，验证了同义词召回与融合排序。报告写入 `artifacts/benchmarks/retrieval-benchmark.md` 并由 CI 上传。真实 embedding 质量仍为 `S8-R1` 遗留项。
- **2026-11-20 [win-x64] [S9 模型评测命令]**：新增 `AiAssistant.Host --benchmark-model [--output <path>]`，复用固定中文基准集输出成功率、JSON 合规率、平均耗时与失败详情；模型不可用时仍写报告并以退出码 1 结束。基准结果新增 `ErrorDetail` 便于定位端点问题。修复本地模型端点在存在系统代理环境变量时误走代理的缺陷（`ModelHttpClientFactory` 对 loopback 默认禁用代理，显式代理仍优先），新增 3 项代理判定测试。用本地模拟 Ollama 端点完成端到端验证：成功路径 100% 成功/100% JSON/退出码 0，离线路径 0% 且报告含 `ModelGatewayException`。将两个浸泡测试放入禁用并行的 `Soak` 集合，消除全量并行时的计时抖动。全仓 126/126 测试通过。真实模型评测仍为 `S5-R1` 遗留项。
- **2026-11-20 [win-x64] [S9 MCP 协议兼容与一致性校验]**：MCP 端点升级为 Streamable HTTP 兼容：支持 `application/json` 与单事件 `text/event-stream` 响应、`Mcp-Session-Id` 会话头、协议版本协商（`2024-11-05` / `2025-03-26` / `2025-06-18`）与 `DELETE /mcp`；新增无头 `--mcp-serve` 与 `--mcp-token` 命令。新增 10 项协议一致性测试（初始化/协商、通知 202、SSE、工具 schema、错误码 -32700/-32601/-32602、会话删除），并新增独立 Python 一致性客户端 `build/mcp/mcp-client.py`。端到端实测：JSON 模式 17/17、SSE 模式 18/18 通过（含未授权 401、写入工具拒绝）。全仓 123/123 测试通过。第三方 MCP 客户端实测仍为遗留项。
- **2026-11-20 [win-x64] [S9 多日浸泡完成]**：新增可配置 3–14 天的加速浸泡 `S9MultiDaySoakTests`（默认 3 天/72 小时，约 16 秒），覆盖持续工作、过载洪峰、黑名单、暂停/恢复、配额耗尽、**8 小时休眠窗口**、保留清理与日志/导出泄露检查；产出 `artifacts/soak/soak-<days>d.md` 报告并由 CI 上传。实测 146/146 控制事件零丢失、144 条黑名单拦截、队列峰值 12/12、289 条事件、72 个媒体、休眠窗口零事件、完整性 `ok`、保留清理 72/72。全仓 113/113 测试通过。真实时间多日浸泡与显示器/麦克风热插拔仍待真机执行。
- **2026-11-21 [linux-x64] [S7-R3 命令音频 opt-in 完成]**：新增 `VoiceSessionOptions.RecordCommandAudio`/`CommandAudioRetention`、`CommandAudioWavEncoder`（16 kHz 单声道 16-bit WAV，无额外编解码依赖）与 `IVoiceSession.SetCommandAudioRecordingAsync`；仅当显式开启且会话接入了 `IMediaStore` 时，唤醒窗口内的命令音频才经 AES-GCM 写入媒体库，`voice_commands.audio_content_id` 记录引用，写入失败只告警不影响命令执行。`AesGcmMediaStore.PurgeExpiredAsync` 在擦除到期媒体时同步把 `voice_commands.audio_content_id` 置空；`MediaRetentionService` 沿用既有周期清理。新增“语音”页“记录命令音频”开关与状态说明，Host 支持 `AIASSISTANT_VOICE_RECORD_COMMAND_AUDIO` 与 `AIASSISTANT_VOICE_AUDIO_RETENTION_DAYS`（1–365，默认 7）。新增 4 项测试（默认不落盘、opt-in 写入 WAV/保留期/引用、无媒体库拒绝、到期置空），`linux-x64` 全量 151/151。
- **2026-11-21 [linux-x64] [S3-R3 Wayland 连续 PipeWire 抓屏完成]**：新增 `AiAssistant.Platform.Linux/PipeWire`（`PipeWireNative` P/Invoke、`SpaPodCodec` 手写 SPA pod 编解码、`PipeWireFrameConverter`、`PipeWireScreenCastStream`），`WaylandScreenCapture` 优先消费 `OpenPipeWireRemote` 的连续帧流，失败自动回退 portal 按需截图；新增 `AIASSISTANT_WAYLAND_PIPEWIRE=false` 关闭开关。常量与结构布局对照 PipeWire 1.6.2 头文件（`SPA_TYPE_Object=15`、`SPA_TYPE_OBJECT_Format=0x40003`、`VIDEO_format=0x20001`、`PW_VERSION_STREAM_EVENTS=2`），并用官方 `spa_pod_builder` 生成的参考字节逐位比对通过。真机验证中发现并修复三处缺陷：① `spa_buffer.datas` 指向首个 `spa_data` 结构而非指针数组，按指针读取导致原生回调段错误；② 格式尺寸上限按 portal 报告的 1920×1080 协商，而 mutter 节点实际只提供 2560×1440 固定尺寸，导致 `no more input formats`，改为 8192 上限宽范围；③ 托管异常穿出 pipewire 回调会终止进程，已全部包裹并降级。GNOME Wayland（2560×1440）真机测试 `LiveWaylandScreenCast_ProducesFrame` 通过，全仓 176/176。
- **2026-11-21 [linux-x64] [S3 portal D-Bus 报文缺陷修复]**：真机联调暴露 `TmdsXdgPortalClient` 两处会直接导致 portal 不可用的缺陷：① `MessageWriter.WriteMethodCallHeader` 参数顺序为 `(destination, path, interface, member, signature)`，原代码把 member 与 signature 传反，总线以协议错误断开连接；② `MessageWriter` 是可变结构体，原实现通过 `Func<MessageWriter, ...>` 委托按值传入，报文体写入落在副本上，`a{sv}` 报文缺失同样导致断连。已重构为每个调用内联写报文、订阅与发送分离（`RequestLease`），并同步修正 `TmdsAtSpiClient` 的两处同样问题。修复后 `CreateSession` 正常返回 `session_handle`，为后续 ScreenCast 打通前提。
- **2026-11-21 [linux-x64] [S4-R2 Linux RapidOCR 接入]**：新增 `AiAssistant.Processing/Ocr`（`RapidOcrOptions`、`RapidOcrImage`、`RapidOcrDetPostProcessor`、`RapidOcrCtcDecoder`、`RapidOcrTextRecognizer`）与 Application 端口 `IImageOcrRecognizer`/`ImageFrameBuffer`；`LinuxTextExtractor` 在 AT-SPI 无文本时用 `LinuxPngCodec` 解码截图并调用按需 OCR。模型目录 `AIASSISTANT_RAPIDOCR_MODEL_DIR`（默认 `<data>/models/rapidocr`，需 det/rec onnx 与 `ppocr_keys_v1.txt`），缺模型时 `Ocr` 能力为 `PermissionRequired` 并回退应用名/窗口标题。过程中发现并修复真实缺陷：先引入 `Microsoft.ML.OnnxRuntime` 1.32 会覆盖发布根目录的 `libonnxruntime.so`，`ldd -r` 报 `VERS_1.28.2 not found`，sherpa ASR/VAD/TTS 会整体失效；改为 `Microsoft.ML.OnnxRuntime.Managed` 1.28.0 复用 sherpa 自带原生库，发布体积保持 149 MB，`ldd -r` 无未解析符号，约束写入 ADR-0007。新增 10 项测试（缺模型降级、真实原生 ORT 加载、DB 后处理、CTC 解码、帧校验、AT-SPI 优先/OCR 兜底/无截图跳过），全仓 169/169，`--startup-check` 通过。真实模型中文截图集质量仍为遗留项。
- **2026-11-21 [linux-x64] [S4-R3 Whisper.net 批处理 ASR 完成]**：新增 `WhisperAsrEngine`（懒加载 `WhisperFactory`、16 kHz 校验、段落概率均值置信度、异常降级）与 `AsrEngineSelection`；Host 通过 `AIASSISTANT_ASR_ENGINE=whisper` 显式切换（默认 sherpa-onnx），模型路径 `AIASSISTANT_WHISPER_MODEL_PATH` 或 `<data>/models/whisper/ggml-small.bin`，缺模型时能力为 `PermissionRequired` 且启动自检照常通过。新增发布裁剪目标，只保留目标 RID 的 Whisper 原生库（`linux-x64` 发布体积 163 MB → 149 MB）。新增 8 项测试（缺模型降级、非 16 kHz 短路、原生运行时加载、引擎选择解析），`Processing.Tests` 15/15；`--startup-check` 与 `dotnet publish -r linux-x64` 实测通过。真实模型识别质量仍属黄金集遗留项。
- **2026-11-21 [linux-x64] [Linux 端 Skia 原生资产补齐]**：`linux-x64` 全量测试暴露 `Report.Tests` 的 4 项 PNG/PDF 栅格化测试因缺少 `libSkiaSharp.so` 失败（`AiAssistant.Export` 只引用了托管 `SkiaSharp`，Linux 原生资产仅由 Host 经 Avalonia 传递引入）。在 `AiAssistant.Export` 增加 `SkiaSharp.NativeAssets.Linux`（3.119.4）并同步 CPM 与依赖清单，修复后 `Report.Tests` 61/61 在 Linux 通过；`generate-sbom.py --check-manifest --verify` 通过（110 组件 / 111 依赖边）。
- **2026-11-21 [osx-arm64] [S7-R2 macOS 音频输出完成]**：Swift Helper 新增 `audio.play` 方法（`AudioPlayback.swift`，AVAudioEngine + AVAudioPlayerNode，float32 单声道 PCM 直接内存渲染，不写临时文件；每次播放独立 engine 并在结束后停止，空闲时不占用音频设备）；`MacOsAudioPlayback` 改为经 `IMacHelperSupervisor` 调用，能力状态随 Helper 健康度切换（Healthy → `Supported`，其余 → `Degraded`），空载荷与超 30 秒载荷分别短路与截断。真机静音回放验证通过：`audio.play` 往返 0.7–1.7 ms，Heartbeat 不受影响。新增 2 项平台契约测试与 2 项 `AIASSISTANT_MAC_HELPER_E2E=1` 真机测试（回放 + TCC 降级），`osx-arm64` 全量 180/180。
- **2026-11-21 [osx-arm64] [Helper 心跳 TCC 阻塞缺陷修复]**：真机探针发现每次 `ping` 都同步调用 TCC，`AVCaptureDevice.authorizationStatus(for: .audio)` 单次耗时 819–1648 ms（冷启动首次握手 1.5–1.9 s，首次运行二进制因系统扫描曾达 21 s），导致所有 IPC 请求被串行阻塞，极端情况下会超时并触发无意义重启。Helper 改为 `PermissionCache`（NSLock 保护的快照 + 后台串行队列刷新，`ping` 永不阻塞在 TCC 上，`permissions.query` 才同步刷新，握手使用阻塞快照保证首帧准确）；`MacHelperOptions` 新增 `HandshakeTimeout`（默认 10 s）与普通请求超时解耦。修复后 `ping` 从约 1000 ms 降至 **0.2–0.3 ms**，并新增 `< 300 ms` 的真机回归断言。
- **2026-11-21 [osx-arm64] [macOS 包布局与启动自检]**：`bash build/package/package-macos.sh --skip-sign --version 0.1.0` 在本机产出 `AiAssistant-0.1.0-osx-arm64.zip`（56 MB，`.app` 155 MB，含 `SHA256SUMS`），Swift Helper 随包进入 `Contents/MacOS/aiassistant-mac-helper` 并被 `ResolveMacHelperPath` 命中。包内 `--smoke-check` 通过、`--doctor` 输出 `storage healthy schema=v2`、`pdfFont=/System/Library/Fonts/Supplemental/Arial Unicode.ttf`、模型端点离线（本机未装 Ollama，预期）。`.pkg`、Developer ID 签名与公证仍需证书，属 `S9-R1`。
- **2026-11-21 [osx-arm64] [macOS GUI 启动主线程缺陷修复]**：打包 `.app` 真机启动暴露 `IDispatcherImpl belongs to a different thread`——顶层 `async Task<int> Main` 在 `await AppRuntime.CreateAsync()` 之后落到线程池线程，而 AppKit/Avalonia 只能在进程主线程初始化，导致 macOS 桌面壳根本无法启动（`--startup-check` 等无头命令不受影响，故此前未暴露）。重构 `Program.Main` 为同步入口：无头命令仍走原异步链；桌面路径由 `RunAsync` 完成初始化并置位 `DesktopShellHandoff`，再回到主线程启动 Avalonia（Linux 的 Wayland→X11 回退同样在主线程序列内执行），运行时在 UI 退出后统一释放。打包 `.app` 真机复测：Host 与 Helper 进程正常常驻、数据目录与加密库创建成功。
- **2026-11-21 [osx-arm64] [TCC 授权弹窗补齐]**：真机发现 Helper 只做 `CGPreflightScreenCaptureAccess` / `authorizationStatus` 预检、从不触发系统授权框，全新安装会永久停在「需要权限」且用户无从授权。新增 `PermissionPrompter`：抓屏预检失败时后台触发 `CGRequestScreenCaptureAccess()`，麦克风 `notDetermined` 时触发 `AVCaptureDevice.requestAccess`，辅助功能缺失时以 `AXIsProcessTrustedWithOptions` 打开授权面板；每次进程仅请求一次、全部在后台队列执行，避免阻塞 IPC 与心跳。`permissions.query` 仍返回真实快照。真机验证：未授权时 `screen.capture` 快速返回 `permission_denied`（3 ms），Helper 不崩溃、心跳正常；授权点击仍需人工完成（`S3-R1`）。
- **2026-11-21 [osx-arm64] [macOS 采集闭环真机打通]**：系统授权弹窗生效后，打包 `.app` 内 Helper 的 `screenRecording` 变为 `granted`，`screen.capture` 返回真实帧。新增运维命令 `--capture-smoke [seconds]`（无头运行采集管线 N 秒并输出落库事件/媒体数，事件为 0 时退出码 1）。真机实测 20 秒：`events=2 media=1 healthy=True`，事件来源 `WorkBuddy`；随后 `--report-range` 在 macOS 生成 Markdown/SVG/PNG/PDF。GUI 首轮 `events=0` 属预期——当时前台为 1Password，命中内置密码管理器黑名单。**「授权 → 抓屏 → 加密落库 → 报告导出」已在 macOS 真机闭环**。
- **2026-11-21 [osx-arm64] [Helper 崩溃监督真机验收]**：`IMacHelperProcessInstance` 新增 `ProcessId`（诊断与故障注入用），新增真机测试 `LiveHelperRecoversFromCrashWithFreshSession`：强杀 Helper 进程后，监督器在指数退避内重新拉起（`RestartCount` 递增、PID 变化、新会话 Token），状态回到 `Healthy`，音频回放能力恢复并再次播放成功。三项真机 Helper 测试（回放 / TCC 降级 / 崩溃重启）全部通过，`osx-arm64` 全量 181/181。同时修正 Helper 缺失时的修复提示路径（`Contents/Helpers` → 实际打包位置 `Contents/MacOS`）。
- **2026-11-21 [osx-arm64] [doctor 增加 Helper 与能力观测]**：`--doctor` 新增 `macosHelper` 行（status / restarts / screen / accessibility / microphone）与全平台能力清单（Kind: Status | Summary），macOS 上会按需拉起 Helper 再释放。真机包内实测：`status=Healthy restarts=0 screen=Granted accessibility=Granted microphone=NotDetermined`，ScreenCapture / WindowActivity / FileActivity / AccessibilityText / Ocr 均为 `Supported`，AudioCapture 为 `PermissionRequired`、WakeWord 为 `Degraded`（语音需显式开启）。顺带修正 Windows/macOS 能力清单里过时的 “Pending S7” 唤醒描述。
- **2026-11-21 [osx-arm64] [S4-R 识别黄金集命令落地]**：新增 `--benchmark-asr` / `--benchmark-ocr`，共享 `AiAssistant.Processing/Benchmarks`：`CharacterErrorRate`（归一化后 Levenshtein，忽略空白/标点/大小写）、`RecognitionSampleSet`（媒体 + 同名 `.txt` 配对，缺参考答案给出警告）、`WavPcm16Reader`（PCM16 任意采样率/声道 → 16 kHz 单声道线性重采样）、`RecognitionBenchmarkEvaluator`（逐样本评分与失败隔离）、`RecognitionBenchmarkReport`（逐样本 CER/编辑数/耗时/文本与警告的 Markdown）。模型缺失时整批标记 `engine unavailable` 而不是记 100% 错误，退出码 1；OCR 仅在注册 `IImageOcrRecognizer` 的平台（Linux RapidOCR）可用。新增 15 项测试（CER、配对、WAV 解码与重采样、评分/不可用路径、报告转义），`Processing.Tests` 37/37；真机跑通空模型场景并正确写出报告。
- **2026-11-21 [osx-arm64] [识别基线首次跑通]**：下载 sherpa-onnx Paraformer 中文 small（`model.int8.onnx`，77.9 MB）到 `artifacts/models`，用 `say` 合成 10 条中文语音（两种普通话音色）与 12 张渲染截图（浅/深色、18px 小字、低对比度、中英混排）构成合成黄金集。**ASR 10/10、平均 CER 12.45%、完全匹配 3/10、热态 17–21 ms（首次 2185 ms 含模型加载）**；**OCR 12/12、平均 CER 0.00%、75–91 ms**。基线详情与典型错误见 `docs/testing/baselines/2026-11-21-macos-arm64.md`。
- **2026-11-21 [osx-arm64] [PNG 滤波缺陷修复 + macOS Vision OCR 适配器]**：基线暴露出 `TryDecodeToBgra32` 在 Sub/Up/Average/Paeth 滤波下用未重建邻居字节，真实截图解码为空白图、OCR 全空——影响 Linux portal/X11 截图路径。改为逐行重建并补 6 项滤波回归测试；同时把 `LinuxPngCodec` 上移为 `AiAssistant.Application.Imaging.PngCodec` 供各平台复用，新增 `MacOsImageOcrRecognizer`（经 Swift Helper 的 `text.extract` 走 Vision），使 `--benchmark-ocr` 在 macOS 可直接运行。全量 204/204。
- **2026-11-21 [osx-arm64] [Whisper 对比基线与 KWS 评测落地]**：下载 `ggml-small.bin`（466 MB）跑同语料对比：Whisper **CER 27.48%**、完全匹配 0/10、热态 214–251 ms，sherpa 为 12.45% / 3/10 / 17–21 ms；Whisper 有 2/10 输出繁体，按简体逐字比较会抬高 CER（折算后仍更差、更慢）。新增 `--benchmark-kws`：`KwsBenchmarkEvaluator` 按 100 ms 帧流式喂入检测器，统计唤醒率、首次偏移（延迟）与误唤醒/小时，`KwsBenchmarkCommand` 负责语料与 Markdown；配套 3 项测试。KWS 模型（zipformer wenetspeech 3.3M int8）+ 拼音 tokens 组出 `kws/` 资产后真机实测：**唤醒率 87.5%（7/8）、误唤醒 0.00 次/小时（负样本 6.0 分钟）、延迟中位数 1100 ms**（唤醒词约 1.1 s，等价词尾触发），唯一未命中为低语速 Grandma 音色。基线详情见 `docs/testing/baselines/2026-11-21-macos-arm64.md`。
- **2026-11-21 [osx-arm64] [KWS 扩样到 2 小时负样本]**：正样本扩到 13 条（9 种中文音色），负样本扩到 **2.12 小时**（12 条 10 分钟乱序拼接长样本 + 10 条单元样本）。结果：**唤醒率 84.6%（11/13）、误唤醒 0.00 次/小时、延迟中位数 1100 ms**；零误唤醒在 2 小时量级仍成立，但新增 Reed 音色与原有低语速 Grandma 各漏检一次，说明阈值对音色/语速鲁棒性不足。评测吞吐约 **100× 实时**（2.12 小时音频耗时 74 秒），可作为发布前常规门禁。随后把 `KeywordsScore`/`KeywordsThreshold` 做成可配置（`AIASSISTANT_KWS_SCORE` / `AIASSISTANT_KWS_THRESHOLD`）：threshold 0.15–0.35 无变化，score 1.0→3.0 把唤醒率提升到 **92.3%** 且误唤醒仍为 0，5.0 回落到 84.6%，故默认值定为 **3.0**。
- **2026-11-21 [osx-arm64] [Linux RapidOCR 基线受阻]**：本机无 Linux 主机、无 Docker/Podman，RapidOCR 的 PP-OCRv4 ONNX 资产在当前代理下也未取到（GitHub release 资产 404、ModelScope 根目录无对应文件）。`--benchmark-ocr` 已支持该适配器，需在 Linux 机器外挂模型后运行；macOS Vision 侧基线仍为 12/12、CER 0.00%。
- **2026-11-21 [osx-arm64] [真人语料基线]**：经 HF 镜像取 `AudioLLMs/aishell_1_zh_test` 一个 parquet 分片（2307 条 AISHELL-1 测试集，16 kHz WAV + 转写），随机抽 100 条做 ASR、693 条拼成 6×10 分钟做 KWS 负样本。结果：sherpa **CER 6.34%**（真人优于合成 12.45%）、Whisper **17.13%**（繁简归一化后；未归一化 26.56%）、KWS 真人负样本 60 分钟 **0 误唤醒**。同时把 OpenCC `TSCharacters.txt`（Apache-2.0，104 KB）内置进 `CharacterErrorRate.Normalize` 做繁简归一，新增 2 项测试；数字书写差异（`2012` vs `二零一二`）仍如实计入 CER。
- **2026-11-21 [osx-arm64] [开发证书签名验证]**：用本机 Apple Development 身份（`zeqian_gao@icloud.com (4XH8AUR7JN)`，Team ID `Y6ANU9Z954`）走完整 `package-macos.sh --identity` 签名：`codesign --verify --deep --strict` 通过、自包含 .NET 的 `.dylib` 加载正常（新增 `disable-library-validation` 权限）、签名后 `--smoke-check`/`--doctor`/`--capture-smoke`（15 秒 2 事件 2 媒体）全通过；`spctl` 仍 rejected（Apple Development ≠ Developer ID 且未公证，属预期）。脚本新增 `--keychain <path>`。`tccutil reset` 在本沙箱内被拒，权限撤销演练需在系统设置或真终端手动完成。
- **2026-11-21 [osx-arm64] [备份/恢复缺陷修复]**：E2E 发现 `--restore` 只能恢复到原数据目录——备份仅含加密 DB，不含密钥库，恢复到冷目录时因密钥不匹配报 `SQLITE_NOTADB`。改为**自包含 zip 备份**（`database/aiassistant.enc.db` + `keystore/` 密钥信封 + `media/` 加密媒体），恢复时先用备份自带密钥信封校验再落地，同机跨目录恢复真机复验通过（2 事件 2 媒体完整性 ok）；旧版裸 DB 备份仍兼容。新增 `BackupIsSelfContained_AndRestoresIntoColdDirectory` 测试；跨机恢复仍受 OS Keychain 包裹限制（设计如此）。
- **2026-11-21 [osx-arm64] [macOS 全功能 E2E 与 pkg/安装]**：`package-macos.sh` 改为 macOS 上**无条件生成 `.pkg`**（证书信任 postinstall 仅在提供 `--certificate` 时附加）。全链路实测：版本/启动自检/诊断/抓屏（打包应用 15 秒 2 事件 2 媒体）/日报四件套/问答（无证据拒绝）/备份恢复（同机跨目录）/重建向量/清除/密钥管理/模型探测/对话基准（100% 成功率、100% JSON、avg 7.3s）/检索基准（hybrid recall 89.6%、MRR 1.0）/MCP（initialize + tools/list，含 Bearer 与 session id）/GUI 常驻。`.app` 以 Apple Development 身份 `codesign --verify --deep --strict` 通过；`.pkg` 56 MB 载荷核对（`/Applications/AiAssistant.app`，156 MB，bundle id com.aiassistant.desktop）。**安装需管理员权限**：`sudo installer -pkg artifacts/packages/AiAssistant-0.1.0-osx-arm64.pkg -target /`（沙箱禁止 sudo，需用户手动执行后按卸载清单核验）。
- **2026-11-21 [osx-arm64] [真实用户场景 UI 端到端自动化]**：在 Avalonia.Headless 之上补充 4 项**驱动真实 `MainWindow` 的完整用户流程**（真实指针点击/键盘/文本输入 → 断言可见 UI 状态变化）：① 切到隐私与控制 → 点「暂停采集」→ 顶部横幅变「已暂停 · 不补采暂停期间内容」→ 点「恢复采集」复原；② Ctrl+F 聚焦检索 → 输入关键词 → 点「检索证据」→ 点「定位」跳回时间线选中被引事件；③ 语音页点「启用语音」→「正在聆听」→ 点「关闭语音」回「语音休眠」；④ 数据与导出两步删除（「删除该日数据」→「确认删除」→「已删除」）。`Click` 采用控件坐标的真实 `MouseDown/MouseUp`，控件失焦/未布局时回退到绑定命令。`Platform.Tests` 62/62。
- **2026-11-21 [osx-arm64] [修复 GUI 不显示的关键时区 bug]**：AX 驱动排查发现应用在非 UTC 机器上**完全无窗口无托盘**——根因是 `ReportsViewModel`/`DataManagementViewModel` 用 `new DateTimeOffset(DateTime.Today, TimeSpan.Zero)` 初始化日期，`DateTime.Today` 的 `Kind=Local` 与 `offset=Zero` 在 GMT+8 下不匹配抛异常，导致 `CreateShellViewModel` 失败、整个桌面外壳没起来（此前 `LogToTrace()` 无监听器，异常被静默吞掉）。改为 `DateTimeOffset.Now.Date` 并补 `SeedLocalTodayWithoutTimezoneCrash` 回归测试。修复后打包应用**窗口正常显示**，`swift tests/ui-e2e/ax-e2e.swift run` 3/3 通过（窗口标题 + 顶部横幅「正在本机采集」「语音休眠」）。局限：Avalonia macOS 辅助功能桥不暴露带名字的按钮/标签，按名点击需坐标点击或补 AX 桥。
- **2026-11-21 [osx-arm64] [UI 深/浅主题 + OCR 坐标点击驱动]**：根因是**主题不一致**——FluentTheme 跟随系统深色变体，但布局颜色全硬编码成浅色，导致控件（标签/输入框）深色渲染叠在浅色背景上、对比度混乱。重构为**语义化动态资源**（`ShellApp` 内 `ThemeDictionaries` 提供 Light/Dark 两套 19 个画刷，`MainWindow.axaml` 全部 `{DynamicResource}`），顶部新增「深色/浅色」切换按钮，`AIASSISTANT_THEME=light|dark` 可固定，默认跟随系统；顺手把 `MutedBrush` 浅色由 #64748B 加深到 #475569、事件来源字号 11→12。浅/深两套截图验收均通过（标签、列表、详情卡片对比度达标）。AX 驱动补齐「**截图 + Vision OCR 定位 + CGEvent 坐标点击**」：`swift tests/ui-e2e/ax-e2e.swift run` **5/5 通过**（窗口/横幅断言 + 切隐私与控制→暂停采集→恢复采集的真实点击流程）。
- **2026-11-21 [osx-arm64] [UI 配色对齐 VS Code Modern + 字号协调]**：按 VS Code Dark/Light Modern 重做两套语义色（中性灰 `#181818/#1F1F1F`、浅色 `#F3F3F3/#FFFFFF`、强调 `#0078D4`），顶栏从藏青改为浅灰/近黑标题栏并加底边框；字号统一为层级刻度（节标题 20→18、内容大标题 23/22→20、子标题 18→15、正文 12、徽章 10），导航标签降到 13px、未选中灰字/选中蓝字加粗；深色次要文字提亮到 `#A8A8A8`、媒体预览加边框分隔。浅/深两套截图逐项验收均通过（对比度、层级、标签字号达标）。
- **2026-11-21 [osx-arm64] [应用图标接入]**：图标原本**有设计**（蓝底渐变 + 白色时钟，`build/package/assets/aiassistant.png`），但三处未接上导致测试看不到：① `.icns` 已进 `.app` 但 `Info.plist` 缺 `CFBundleIconFile` → 访达/Dock 显示通用图标，已补 `CFBundleIconFile=AiAssistant`；② 托盘/窗口图标 `CreateApplicationIcon()` 是代码画的橙色十字占位图（字节序还写反成了橙色），改为从嵌入资源加载真实时钟 PNG；③ 图标仍是旧藏青 `#3157D5`，`generate-icon.py` 改用新强调色 `#0078D4→#005A9E` 并重新生成 PNG/ICO/MSIX 资产。验证：`CFBundleIconFile=AiAssistant`、`AiAssistant.icns` 1024×1024 有效、应用带嵌入图标正常启动。
- **2026-11-21 [osx-arm64] [应用显示名 + 日期控件微调]**：`ShellApp` 构造器设 `Name="时光"`，覆盖 Avalonia 默认占位名「Avalonia Application」，Dock/菜单栏显示名更正（真机 `localizedName=时光`）。日期控件：给 `TextBox/ComboBox/DatePicker` 加主题化输入背景/边框（`InputBackgroundBrush`/`InputBorderBrush`），日期输入框在深色下边框清晰可见；给日历弹窗加独立背景/边框（`DatePickerFlyoutPresenterBackground`/`BorderBrush`）与页面区分。
- **2026-11-21 [osx-arm64] [日期控件自定义 + 检索页日期框重叠修复]**：`DatePickerPresenter` 位于 Popup 独立视觉树，Window.Styles 无法覆盖；改为在 `MainWindow` 里给 4 个 DatePicker 挂 `Loaded`，拿到模板 Popup 订阅 `Opened`，弹窗打开时直接把 `PART_AcceptButton`/`PART_DismissButton` 的 Content 换成文字「确定」「取消」TextBlock，并给 ScrollViewer 上下渐隐留白替代硬裁切；同时强制 `Placement=Bottom` 不让弹窗翻到标签栏上方。检索页两个 DatePicker 原先并排重叠，改为上下分行 + 「从」「到」标签。真机截图验收：文字按钮生效、三列高亮对齐、检索页不重叠。全量 217/217、0 警告。
- **2026-11-21 [osx-arm64] [真人场景功能测试（CLI + 真机 GUI）]**：以隔离数据目录 `/tmp/aia-persona` 按真人使用顺序逐功能验收，17+ 个命令全通：①`--version`/`--smoke-check`/`--startup-check`/`--doctor`（storage healthy、各能力矩阵正常）；②`--capture-smoke 20` → events=2 media=2 落库 integrity=ok；③`--report-range` 生成 md/svg/png/pdf/stats 五件套；④`--ask` 有证据/无证据两条路径；⑤`--backup` → 冷目录 `--restore`（确认门禁生效，恢复后 2 事件 2 媒体完整）；⑥`--purge-data` 确认门禁；⑦`--probe-model`、密钥导入/列表/删除门禁；⑧检索基准 hybridRecall=89.6%、MCP initialize/tools/list（4 只读工具，未授权 HTTP 401）；⑨GUI 4 个真人流程（headless 真实窗口）全过。**发现两处真人体验问题**：(A) 采集后直接 `--ask` 返回"无证据"，必须手动 `--rebuild-vectors` 才有证据——用户不会知道这步；(B) `--benchmark-model` 在 chat 端点不可用时 `success=0%` 却退出码 0，脚本会误判成功。两项待评估修复。
- **2026-11-21 [osx-arm64] [修复问答需手动建索引 + 真人 UI 场景测试]**：(A) 修复"采集后直接问答无证据"——`HybridSearchService` 增加可选 `ensureIndexed` 回调，向量检索前进程内只触发一次：若 `IVectorIndex.IndexedCount==0` 且有事件则 `RebuildAllAsync`。由 AppRuntime 注入，CLI/GUI/语音三处检索统一生效，启动仍不调模型。新增 `Search_EnsuresIndexOnceBeforeVectorRecall` 测试；真机验证采集后不手动 rebuild 直接 `--ask` 即返回 E1/E2 证据。(B) 复核"benchmark-model 退出码"为测试误报（管道 `| tail` 让 `$?` 取了 tail 的码；实际 `return report.SuccessRate > 0 ? 0 : 1` 正确，真机退出码=1），代码无需改。新增 `tests/ui-e2e/persona-ui.swift`：截图+OCR 定位+CGEvent 坐标点击，在真实 .app 上逐页真人操作，14/14 通过（7 页导航、暂停/恢复采集、语音启用响应、日报生成、诊断重检、主题按钮文案）。语音模型资产未安装时启用降级为提示，属环境限制。全量 218/218、0 警告。
- **2026-11-21 [osx-arm64] [语音模型安装 + ollama 完整流程 UI 测试]**：发现 GUI 语音页「model assets are not installed」根因是打包应用未带模型且默认数据目录无资产——KWS(zipformer 3.3M) 与 ASR(Paraformer zh) 模型实际已下载在 `artifacts/models/`（KWS 在 `kws/` 子目录、ASR 在根，恰与 `SherpaOnnxOptions` 路径结构匹配）。新增 `build/models/install-voice-models.sh` 把两类模型装入 `<数据目录>/models/sherpa/`（无需设 `AIASSISTANT_SHERPA_MODEL_DIR`），新增 `docs/MODELS.md` 说明 ollama 模型名（本机 chat 实际是 `gemma4:12b-mlx`，默认 `gemma4:12b` 会导致 probe chat=False）与安装/复现步骤。ollama 接入验证：`--probe-model` chat=True embed=True、`--benchmark-model` 100%/100%/6.5s。新增 `tests/ui-e2e/ollama-flow.swift`：真实 .app 上语音就绪→检索(embed)→问答(chat)→日报完整链路，**7/7 通过**（语音状态变「Wake word ready; TTS feedback unavailable」、检索命中、回答元信息 `模型 gemma4:12b-mlx · 引用 E1..E5`、日报生成）。遗留质量点：chat 回答被判 `uncited-model-answer` 降级为抽取式（直接 curl ollama 同 prompt 能正确输出 [E1]，疑为应用侧 PromptMinimizer/redaction 改写 system prompt 所致），回答仍带引用列表但非模型自然语言组织，待评估。全量 218/218、0 警告。
- **2026-11-22 [osx-arm64] [修复问答 uncited 降级]**：根因是 gemma 在证据多条时把引用合并输出为 `[E1, E2, E3]`，而 `CitationRegex` 只匹配单条 `\[E(\d+)\]`，导致 citedIds 为空 → 判 uncited → 降级为抽取式。改为匹配 `E(\d+)`（兼容 `[E1]`、`[E1][E2]`、`[E1, E2, E3]` 与裸 `E1`）。新增 `Answer_WithMergedCitationBracket_KeepsModelAnswer` 测试。真机验证 `--ask` 由 degraded=True 变 degraded=False、回答保留模型原文 `今天使用了 WorkBuddy [E1, E2, ...]`；GUI 完整流程复验回答元信息变「引用 E1..E8 · 证据充分」。全量 219/219、0 警告。
- **2026-11-22 [osx-arm64] [UI 配置模型（chat/embedding/baseUrl）]**：新增 `IModelConfigurationAdmin`（Application 端口）与 `ModelConfigurationAdmin`（Models 实现）：读当前 LocalOffline 端点、保存时只改该端点的 baseUrl/chatModel/embeddingModel 并**保留其它端点与策略**，序列化用 camelCase 与 `models.example.json` 一致，写临时文件后原子覆盖；非 loopback 地址拒绝。`ModelsFileDocument`/`ModelEndpointDocument`/`ModelPolicyDocument` 由 class 改为 record（支持 with 表达式）。诊断页「本机模型网关」卡片新增「本机模型配置」编辑区（对话模型/Embedding 模型/Base URL + 保存按钮 + 状态提示「重启生效」），`DiagnosticsViewModel` 注入 admin 并暴露绑定与 `SaveModelConfigurationCommand`。新增 2 项测试（改 LocalOffline 保留其它端点、拒绝非 loopback）；persona-ui 增补诊断页编辑区渲染断言。全量 221/221、0 警告。
- **2026-11-22 [osx-arm64] [UI 模型配置支持外部模型]**：诊断页模型配置区新增「模型来源」下拉（本机 Ollama / 企业内网 / 公网 API），`ModelConfigurationView` 增加 `Tier` 字段。保存时：LocalOffline 端点强制 loopback（否则拒绝并提示改选内网/公网）；内网/公网允许远程地址并自动 `allowRemoteEndpoint=true`、provider 设为 openai 兼容，**始终保留 LocalOffline 兜底**。公网层仍受 `AllowPublicCloud` 策略与运行期同意门禁约束。新增 `ModelConfigurationAdmin_RejectsRemoteUrlForLocalOffline_ButAllowsRemoteTiers` 测试。全量 221/221、0 警告。
- **2026-11-22 [osx-arm64] [配置内网/公网后即可用]**：模型请求 tier 此前硬编码 LocalOffline（`ModelCompletionRequest`/`ModelEmbeddingRequest` 默认 LocalOffline，问答/检索不走 defaultTier），导致配了内网/公网端点也不生效。改为 `HybridSearchService` 与 `EvidenceQuestionAnswerUseCase` 增加 `tier` 参数，AppRuntime 以 `configuration.Policy.DefaultTier` 注入，问答/检索/日报请求随之路由到默认层。`ModelConfigurationAdmin.SaveAsync` 保存时把 `policy.defaultTier` 切到所选层；公网同时 `allowPublicCloud=true` + `requirePublicCloudConsent=false`（UI 选择公网即显式同意），内网保持 `allowIntranet=true`；始终保留 LocalOffline 兜底。新增 `ModelConfigurationAdmin_PublicCloud_EnablesCloudAndBecomesDefault` 测试，并补 defaultTier 断言；真机验证 defaultTier=CorporateIntranet 时 probe 走内网端点（`gemma4:12b-mlx` chat=True）。全量 222/222、0 警告。
- **2026-11-22 [osx-arm64] [chat/embedding 独立端点 + 故障不回落]**：模型端点从「每 tier 一个（chat+embed 共用一个 baseUrl）」升级为 chat 与 embedding 独立配置。数据模型：`ModelsFileDocument` 新增可选 `Embedding`（独立 `EmbeddingEndpointDocument`：tier/provider/baseUrl/model/密钥），`ModelsConfiguration` 新增 `EmbeddingEndpoint`，loader 解析并校验（LocalOffline 强制 loopback、远程需 allowRemoteEndpoint）。路由：新增 `SplitChatEmbeddingGateway`（CompleteAsync 走 chat 链、EmbedAsync 强制 embedding 端点 tier），`RegisterReports` 构造 chat（TierRouting）+ embedding（独立 EndpointModelGateway）两条独立门禁链。故障处理：`TierRoutingModelGateway` 移除 CorporateIntranet→LocalOffline 的自动回落（`GetGateway`/`CompleteAsync`/`EmbedAsync` 的 fallback 全删），问答层 catch 异常时把具体错误透传为「模型不可用：{message}」供 UI 提示。UI：诊断页模型配置拆成「对话模型」「Embedding 模型」两块，各含来源下拉 + 模型名 + Base URL；`ModelConfigurationAdmin` 读写 chat endpoint 与独立 embedding 文档（同 tier 省略 embedding 文档）。新增 1 项独立 embedding 端点测试并改写回落测试为「不回落」；真机负向验证 embedding 指向不可达端口时 rebuild-vectors 报 Connection refused（证明未复用 chat 端点）。全量 223/223、0 警告。
- **2026-11-21 [docs] [实施清单与进度对齐]**：`IMPLEMENTATION.md` 第 10 节改为与 `PROGRESS.md` 第 5 节同构的 `*-R` 编号：补记 `S6-R2`（密钥导入）、`S8-R`（embedding 95.8% / MCP Inspector / SVG-PNG-PDF）为已完成，拆分 `S5-R1`/`S5-R2`、`S6-R1`、`S7-R1`/`S7-R2`、`S9-R1..R4`、`S3-R1`/`S3-R2`，并新增第 6 节执行顺序表。
- **2026-11-22 [linux-x64] [局域网模型真人场景 E2E + 遗留项收口]**：本机 `Ubuntu 26.04 + GNOME Wayland`（`.NET SDK 10.0.115`）接续，将默认档切到内网端点 `http://192.168.1.109:11434`（`gemma4:12b-mlx` 对话 + `embeddinggemma-2:740m` 向量，`allowRemoteEndpoint=true`、`defaultTier=CorporateIntranet`）。①**真人场景端到端**：`AIASSISTANT_CAPTURE_DIRECTORIES` 白名单目录 + inotify 文件活动产生 10 条真实事件落库 → `--rebuild-vectors`（embeddinggemma）→ `--report-range` 生成 md/svg/png/pdf/stats → `--ask` 用 `gemma4:12b-mlx` 输出带 `[E1..E8]` 证据引用的自然语言回答（`degraded=False`）。`--benchmark-model` 成功率 67%、JSON 100%、avg 17.4s；`--benchmark-retrieval` hybrid recall 89.6%/MRR 1.000。Headless UI 端到端 9/9、全量 224/224、0 警告。②**修复真实缺陷**：`ActivityRangeReportService` 未把 `DefaultTier` 传给 `ModelCompletionRequest`（沿用默认 LocalOffline），导致内网/公网默认档下区间报告永远降级为 extractive；补 `tier` 参数 + `RegisterSearchAndLocalApi` 注入 + 回归测试 `Report_ForwardsConfiguredTierToModelCompletion`，修复后 `--report-range` 用真实模型 `degraded=False`。③**S4-R2 Linux RapidOCR 首条基线**：外挂 PP-OCRv4 det/rec + 字典，12 张合成截图 **CER 30.81%**（0/12 完全匹配，逐样本见 `artifacts/benchmarks/ocr-linux.md`）。④**S4-R1/S7-R1 Linux 引擎冒烟**：sherpa-onnx Paraformer 与 KWS zipformer 原生运行时在 Linux 正常加载（ASR 7/7 转写含中英混说与方言、KWS 7 条负样本 0 误唤醒；合成/真人 CER 与唤醒率基线仍以 macOS 为准）。⑤**S5-R2/S9-R2 浸泡**：3 天加速、全天验收、2 分钟真实时钟浸泡均通过（90 分钟+与热插拔仍待真机）。⑥**S9-R4**：SBOM 重新生成 115 组件/116 依赖、校验通过。⑦**S9-R3 Linux 包**：`package-linux.sh` 产出 tar.gz 62MB + deb 48MB + `SHA256SUMS`，便携解压后 `--version`/`--smoke-check`/`--doctor`（连内网模型）均通过；`dpkg -i` 实装需 sudo 留待真机。
- **2026-11-22 [linux-x64] [真实启动 UI 的端到端测试 `--ui-e2e`]**：真机确认 GUI 可在 Ubuntu 26.04 + GNOME Wayland 上真实启动（原生 Wayland 与 `AIASSISTANT_LINUX_BACKEND=X11` 两种后端均验证，窗口标题「时光 · AI 个人活动追踪助手」、真实窗口活动事件落库）。发现 GNOME Wayland 会拦截 XTest 外部输入注入（`xdotool` 指针/键盘均无效），且 Avalonia 不向 AT-SPI 暴露控件树，因此新增**进程内自驱动真实 UI E2E**：`UiE2ERunner` 在真实 Avalonia 窗口上按脚本切换 7 个页面并执行暂停/恢复、语音启停、数据两步删除、诊断刷新、主题切换，每步用 `RenderTargetBitmap`（`PngBitmapEncoderOptions`）输出 PNG，写出 Markdown 报告并以退出码 0/1 结束。`--ui-e2e` 由 Host 映射为 `AIASSISTANT_UI_E2E`/`AIASSISTANT_UI_E2E_SHOW`，输出目录 `AIASSISTANT_UI_E2E_OUTPUT`。真机实测 **11/11 通过**（含真实窗口标题、暂停横幅「已暂停 · 不补采暂停期间内容」、语音「Wake word ready」、诊断页 10 项能力与内网模型端点、主题深/浅切换），截图与报告写入当次 `artifacts/e2e/ui-<时间>/`。修复真实桌面退出时异步服务释放阻塞导致进程不退出的问题（写报告后 5 秒内确定性 `Environment.Exit`）。全量 224/224、0 警告。
- **2026-11-22 [linux-x64] [全流程真人 UI 测试 + 托盘/图标/退出修复]**：①**全流程真实 UI 测试**：`--ui-e2e` 扩展为 16 步完整用户旅程（时间线 → 真实 embedding 检索 → 真实模型证据问答 → 真实模型周报 → 暂停/黑名单/快照 → 恢复 → 语音启停 → 诊断刷新 → 真实模型日报 → 导出 Markdown → 主题切换 → 数据两步删除 → 退出入口），Host 在 `--ui-e2e` 时先初始化 Schema 并写入 10 条贴近真人的本机工作事件（同一活动片段内，避免 10 次 map 调用）。真机 Ubuntu 26.04 + GNOME Wayland **16/16 通过、50 秒、退出码 0**。②**修复结构化日报永远失败**：`ReportJsonSchema.TryParse` 要求 `standup.today` 非空，但提示词明确允许「无法确定的计划用空字符串」，真实本机模型按提示返回空 `today` → 三次重试全部 `schema-violation`、日报永远生成失败；放宽校验并新增回归断言（空 standup 必须被接受）。③**修复推理型模型拖慢/空响应**：`gemma4:12b-mlx` 具备 `thinking` 能力，默认思考会把输出预算耗尽——`--benchmark-model` 由 67%/17.4s 提升到 **100%/0.86s**（`--ui-e2e` 从 366 秒 2 失败变为 50 秒全通过）。`ModelGatewayOptions.DisableThinking`（默认 true）在 Ollama 请求中发送 `think: false`，无思考能力的模型会忽略该参数。④**无托盘时窗口可正常退出**：GNOME 默认无 StatusNotifierItem，窗口关闭原本只是隐藏且无法再打开；新增顶部「退出」按钮（`RequestExitCommand`）并在托盘不可用时让关闭窗口直接退出（`ShellViewModel.ExitOnClose`，由 `--doctor`/能力矩阵判断）。⑤**图标与桌面身份**：Linux 上设置 Wayland `app_id=aiassistant`（`WaylandProperties.SetAppId`），X11 `WM_CLASS=AiAssistant.Host` 已实测；新增 `build/install-desktop-entry.sh` 安装用户级 `aiassistant.desktop` 与 hicolor 图标（`Icon=aiassistant`、`StartupWMClass=AiAssistant.Host`），Dock 与顶栏按 desktop 文件解析真实图标。全量 224/224、0 警告。
- **2026-11-22 [linux-x64] [模型思考开关/关闭语义/中文化与通用本机来源]**：①**思考开关与强度**：`models.json` 端点新增 `think` / `thinkingLevel`，`ModelGatewayOptions` 以 `Think`+`ThinkingLevel` 取代 `DisableThinking`，Ollama 请求发送 `think: false | true | "low|medium|high"`；诊断页「模型配置」新增「允许思考」复选框与「思考强度」下拉（低/中/高，未勾选时禁用），保存后写入配置并在重启生效。新增 `OllamaHttpBackend_SendsThinkingLevelWhenEnabled`、配置持久化断言。②**失败原因可见**：检索/问答与日报的异常处理不再只给通用提示，直接展示 `ex.Message`（模型不可达、结构化输出被拒、策略拦截等）；诊断页模型端点与权限提示改为中文。③**关闭语义调整**：点击窗口关闭按钮只隐藏窗口、采集继续在后台运行，只有顶部「退出」按钮（或托盘菜单）才退出；新增单实例激活（`<数据目录>/instance.sock`），再次启动只唤起已有窗口并提示「已在运行」，不重复启动采集；无托盘（GNOME 默认）时启动直接显示主窗口。④**UI 全面中文化**：平台能力摘要、权限提示、语音/OCR/Portal 文案、`LOCAL ONLY`、`Base URL`、检索召回方式等统一为中文（17 个文件 107 处）。⑤**本机来源通用化**：模型来源「本机 Ollama」改为「本机」，保存时保留已有 provider，新端点探测 Ollama `/api/tags`，否则按 OpenAI 兼容接口处理（可对接 LM Studio/vLLM），新增 `ModelConfigurationAdmin_PreservesNonOllamaLocalProvider`。`--ui-e2e` 扩展为 17 步（新增「保存思考开关与强度」），并为 E2E 复制私有 `models.json` 避免污染共享配置；真机 **17/17 通过、48 秒、退出码 0**。全量 226/226、0 警告。
- **2026-11-22 [win-x64] [真人 UI 全流程 + 90 分钟浸泡 + 图表刻度修复]**：①**真实窗口 UI E2E**：默认档切到局域网 `http://192.168.1.109:11434`（`gemma4:12b-mlx` 对话 + `embeddinggemma-2:740m` 向量，`defaultTier=CorporateIntranet` 且保留 `LocalOffline` 兜底），`--ui-e2e` 17 步全流程（时间线 12 事件 → 真实 embedding 检索命中 8 条 → 证据问答 `引用 E1 · 证据充分` → 周报 → 暂停/黑名单/恢复 → 语音（缺资产，跳过）→ 诊断 10 项能力 → 保存思考开关 → 日报 v1 → 导出 Markdown → 主题切换 → 数据两步删除 18 事件 → 关闭仅隐藏）**连续 3 次通过、退出码 0**，截图与 `report.md` 归档 `artifacts/e2e/ui-20261122-170737/`。②**交互级证据**：用临时 UIA 驱动（非产品代码）枚举 7 个标签页全部 `enabled=True` 并逐一切换，经无障碍树触发「检索证据」与「重新检查平台能力」，验证命中测试与启用态。③**修复真实缺陷**：区间报告 Y 轴固定 4 段导致小样本刻度重复（3 条事件显示 0/1/2/2/3）——新增 `ChartAxis` 统一 SVG/Skia/PDFsharp 三处渲染，现为 0/1/2/3，并补 `RangeReportSvg_WithSmallMaximum_RendersDistinctYAxisTicks`；日期下拉在英文系统区域显示 `2026年11月22日 Sunday` → 星期固定 `zh-CN`；诊断页同时显示「模型端点已就绪」与「尚未探测或没有可用端点」→ 改为「尚未探测端点能力」。④**S9-R2 90 分钟真实时钟浸泡**：5402.3s、2523 轮、控制 32/32、黑名单 84/84、事件 4727、媒体 2348、暂停窗口事件 0、完整性 ok；外部每分钟采样 81 点：工作集 60.7–66.9MB（末值 61.3）、私有内存 20.4–22.7MB（末值 21.3）、句柄 428–464（末值 444），无单调增长。⑤**S5-R1**：`--benchmark-model` 成功率 100%、JSON 合规 100%、平均 783ms（999/705/645ms）；`--probe-model` 显示 structured/tools=False，但 JSON 合规仍 100%，说明走的是提示词兜底而非原生结构化输出。⑥**受限项**：本机 `dotnet test` 因 VSTest 宿主回环 socket 崩溃（`0xC0000005`）无法执行，改用进程内宿主加载同一测试程序集；MSI 为 `perMachine`，当前非管理员会话无法实装；GitHub Actions 因账单/支出限额未启动。全解决方案 Release 0 警告。
- **2026-11-22 [win-x64] [安装 KWS 唤醒词模型，语音步骤转绿]**：从 `k2-fsa/sherpa-onnx` 的 `kws-models` 发布下载 `sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01`（32MB），按 `encoder/decoder/joiner` 重命名 epoch-12 int8 三件套并写入 `keywords.txt`（`n ǐ h ǎo sh í g uāng @你好时光`），安装到 `<数据目录>/models/sherpa/kws`（4.9MB）。`--doctor` 不再报缺资产；`--ui-e2e` 由 17 步 1 跳过变为 **17/17、0 跳过、退出码 0**，第 10 步详情 `status=语音休眠`，语音页显示「唤醒词已就绪；命令识别模型缺失」（KWS 可用、ASR/TTS 仍缺，属降级但可启用）。②**安装包依赖评估**：`dotnet publish win-x64` 自包含产物 243MB/316 文件，全量 `objdump` 扫描无 `MSVCP140`/`VCRUNTIME140` 导入，干净 Windows 无需 .NET 与 VC++ Redistributable；MSIX 最低 Windows 10 2004；当时安装包不含任何模型资产（随后已改为随包分发，见下条），已写入 `docs/MODELS.md` §2.1 与 `KNOWN-LIMITATIONS.md`。
- **2026-11-22 [win-x64] [三平台安装包随带本机语音模型]**：①**应用侧解析**：`AppRuntime` 新增 `ResolveModelDirectory/ResolveModelFile`，顺序为 `AIASSISTANT_*_MODEL_DIR` → `<数据目录>/models/...` → `<应用目录>/models/...`（sherpa 用 kws/encoder、ASR、VAD、TTS 作为存在标记；rapidocr/whisper 同理）。②**模型载荷**：新增 `build/models/stage-bundle.sh` 汇总 `sherpa-kws`、`sherpa-onnx-paraformer-zh-small-2024-03-09`、`sherpa-tts`、`sherpa-vad` 为 `artifacts/models/bundle`（126MB、11 文件：KWS 4.9MB + ASR 78MB + VITS int8 39MB + VAD 2.3MB）；新增 `fetch-bundle.sh` 从官方 release 拉取并解压（KWS/ASR/TTS/VAD，缓存到 `artifacts/models/downloads`）。③**三平台打包**：Windows zip/MSIX 与 MSI、macOS `.app`、Linux tar.gz/deb 都在 publish 后复制 bundle 到应用目录 `models/`；缺 KWS/ASR/VAD/TTS 任一文件直接失败（开发调试可用 `-SkipModelBundle` / `--skip-models`）。实测产物：zip **188MB**（含 11 个模型文件）、MSI **173MB**（文件表含 `models\sherpa`）、Linux tar.gz/deb **163MB**（`usr/lib/aiassistant/models/sherpa`）、macOS `.app` **285MB**（`Contents/MacOS/models/sherpa`）。④**干净数据目录验证**：用打包暂存目录、空数据目录跑 `--ui-e2e` **17/17、0 跳过**，语音页显示「本机唤醒与语音命令已就绪」；`--benchmark-asr` 用合成 WAV 成功加载 Paraformer（succeeded=1），TTS 冒烟合成 1.78s/8000Hz。⑤**发布工作流**：`release.yml` 三个作业增加 `fetch-bundle.sh`，打包前自动拉取官方模型；`docs/MODELS.md`、`build/package/README.md`、`RELEASE-CHECKLIST.md` 同步更新。⑥**仍外置**：对话/Embedding 模型（Ollama 约 8.3GB 或内网/公网端点）；Whisper（466MB）、RapidOCR、说话人分离为可选资产，用 `--with-whisper` / `--with-rapidocr` 或手工放入数据目录。⑦**合规**：新增 `docs/compliance/MODEL-ASSETS.md` 记录来源与许可证（KWS Apache-2.0、Silero VAD MIT；ASR/TTS 归档无 LICENSE，发布前需确认并补 `THIRD-PARTY-NOTICES`）。

---

## 5. 遗留项与门禁豁免（按用户决定暂缓）

> 这些项目不阻塞 S4–S8 的代码开发，但在 S9 发布门禁前必须完成。

| ID | 来源 | 遗留内容 | 风险 | 后续处理 |
|---|---|---|---|---|
| `S3-R1` | S3 macOS | 🟡 真机已完成：GUI 主线程启动、系统授权弹窗、屏幕录制与辅助功能授权、抓屏→加密落库→报告导出闭环、Helper 崩溃重启、未授权降级、**本机 Apple Development 身份完整签名且签名后采集正常**；剩余权限撤销演练与公证（Developer ID） | 正式公证仍是唯一未验证环节；撤销演练受沙箱限制只能手动 | 手动在系统设置撤销一次屏幕录制并复测；取得 Developer ID 后走公证 |
| `S3-R2` | S3 Linux | 🟡 GNOME Wayland 的 ScreenCast 授权与连续抓屏已真机实测（2026-11-21）；KDE/KWin、X11 回退、AT-SPI 与托盘能力矩阵仍待人工验收 | 合成器差异可能影响窗口与抓屏可用性 | 在 KDE Wayland 与 X11 会话逐项签字 |
| `S3-R3` | S3 Linux | ✅ 已实现连续 PipeWire 帧消费（私有 `pw_stream`、1 秒节流、格式协商与 YUV 降级回退），GNOME Wayland 真机通过（2026-11-21） | KDE/KWin 与多显示器场景未实测 | 在 KDE Wayland 复跑同一真机测试 |
| `S4-R1` | S4 语音 | 🟡 真人语料基线已跑（AISHELL-1 测试集 100 条）：sherpa Paraformer 中文 small **CER 6.34%**、热态 17–21 ms；合成语料为 12.45%。噪声/口音/多人场景仍未覆盖 | 干净朗读语料不等于真实使用（噪声、口音） | 补 AISHELL 噪声测试集或真实会议录音后复跑 `--benchmark-asr` |
| `S4-R2` | S4 OCR | 🟡 macOS Vision 基线 0.00%（2026-11-21）；**Linux RapidOCR 首条基线已跑（2026-11-22）：12 张合成截图 CER 30.81%**（轻量 CPU 模型 vs Vision 差异预期，代码/低对比/暗色样本偏弱，详见 `artifacts/benchmarks/ocr-linux.md`） | 合成截图 ≠ 真实 UI；RapidOCR 在代码/英文/低对比场景质量有限 | 补真实截图集并评估是否按场景切换 OCR 引擎 |
| `S4-R3` | S4 语音 | 🟡 Whisper.net 已跑真人对比（`ggml-small`）：AISHELL-1 100 条 **CER 17.13%**（未归一化 26.56%）、热态 214–251 ms，明显落后 sherpa（6.34%）；数字书写差异仍计入 | 中文口语/新闻域明显弱于专用模型；更大模型未测 | 可对比 `ggml-medium/large` 或换 prompt 优化后复测 |
| `S5-R1` | S5 模型 | 已实测 `gemma4:12b`（本机 100%/100%/4.6s）与局域网 `gemma4:12b-mlx`（think 修复前 67%/17.4s；**2026-11-22 `win-x64` 复测 100%/100%/783ms**，probe 显示结构化/工具=False，但 JSON 合规仍 100%，走提示词兜底）；`--ui-e2e` 中问答/周报/日报均用该端点真实生成；其它候选模型与内存曲线仍未评测 | 不同模型质量/延迟差异较大，默认档位未定 | 对目标候选模型分别运行 `AiAssistant.Host --benchmark-model`，按质量/延迟选默认档 |
| `S5-R2` | S5 调度 | 每日定时、启动补偿与失败退避尚未进行多日浸泡 | 长时间休眠/跨日边界可能暴露时序问题 | S9 浸泡测试覆盖跨午夜、休眠唤醒、模型离线与重复触发 |
| `S6-R1` | S6 企业端点 | 真实企业 mTLS、企业 CA、代理和公网 API 未联调；公网同意仅代码级门禁 | TLS/代理环境差异可能导致端点不可用 | 在企业网络与公网沙箱分别验证握手、证书轮换、代理认证、断网回退与策略审计 |
| `S6-R2` | S6 密钥 | 已提供 `--import-model-secret`（stdin、终端隐藏输入）、列表/删除命令与诊断页密钥卡片；值只写不读 | 仍需用户自行准备密钥值并确保不进入 shell 历史 | 发布说明中提示使用 stdin 管道，避免命令行明文 |
| `S7-R1` | S7 语音 | 🟡 KWS 基线已跑：合成 13 正（9 音色）/ 2.12 小时负 + **AISHELL 真人负样本 60 分钟**，`KeywordsScore=3.0` 唤醒率 **92.3%**、误唤醒 **0.00 次/小时**、延迟中位数 1100 ms；`p09-reed` 漏检、真人正样本（唤醒词实拍）仍缺 | 真人负样本已覆盖；真人「你好时光」正样本无法公开获取，仍需自制 | 用户自录唤醒词正样本复测召回；补 TTS 客观音质指标 |
| `S7-R2` | S7 macOS | ✅ 已实现 Swift Helper `audio.play`（AVAudioEngine 内存 PCM、不落盘、每次播放后释放设备）并真机验证：回放往返 0.7–1.7 ms、心跳 0.2–0.3 ms、空载荷短路、30 秒截断；能力随 Helper 健康度切换 | 签名 `.app` 内尚未复听 | 随 `S9-R1` 在打包应用内复听一次并记录 |
| `S7-R3` | S7 隐私 | ✅ 已实现显式开关、AES-GCM 加密音频引用与到期自动清理（2026-11-21）；仅缺内置回听界面 | 默认仍不落盘，风险已消除 | 需要回听时从加密媒体库导出；内置播放器另立需求 |
| `S8-R1` | S8 检索 | 已完成真实评测：embeddinggemma-2:740m **95.8%**（MRR 1.0，768 维，热态约 19ms）；已把官方查询指令与 `title/text` 文档格式接入生产并支持向量重建 | 合成语料仍与真实分布不同；个别同义词会漂移 | 用真实脱敏语料扩充查询集，按失败样本调参 |
| `S8-R2` | S8 MCP | 已完成 MCP Inspector 2.16.0 真实客户端联调：initialize、tools/list、activity_stats/search_events、未授权退出码 3、写工具退出码 5；其他客户端（Claude Desktop 等）未测 | 其他客户端的会话/SSE 细节可能仍有差异 | 需要时用其他客户端复跑同一流程 |
| `S8-R3` | S8 周报 | 已提供 SVG/PNG/PDF；PDF 自动发现系统 TrueType 中文字体并嵌入子集、文本可检索，无字体时回退矢量轮廓 | 极简 Linux 可能无任何 CJK TTF | 安装 CJK 字体或通过 `AIASSISTANT_PDF_FONT_PATH` 指定 |
| `S9-R1` | S9 签名 | Windows Authenticode/MSIX 与 macOS Developer ID/公证未用真实证书执行；脚本与工作流已就绪 | 未验证的签名链可能导致安装被拦截 | 配置证书与公证凭据后运行 `release.yml`，并保存 `signtool verify` / `spctl` 输出 |
| `S9-R2` | S9 浸泡 | ✅ **2026-11-22 `win-x64` 90 分钟真实时钟浸泡通过**：5402.3s、2523 轮、控制 32/32、黑名单 84/84、事件 4727、媒体 2348、暂停窗口 0、完整性 ok；外部 81 点采样无增长（工作集 60.7–66.9MB、私有 20.4–22.7MB、句柄 428–464、线程 10–13）；此前 14 天加速浸泡与 60 分钟浸泡亦通过；**真机显示器/麦克风热插拔未执行** | 热插拔与多显示器切换未验证 | 在真机插拔显示器与麦克风后重跑 30 分钟段并记录设备变化事件 |
| `S9-R3` | S9 人工验收 | 自动化已覆盖 7 个标签页无障碍名称/加速键、Ctrl+D1…D7 与 Ctrl+F 真实按键、关键控件名称；macOS `.pkg` 已生成并核对载荷，实际安装/升级/卸载需管理员权限（沙箱禁 sudo）；**2026-11-22 `win-x64` 复核：当前会话为普通用户，`Scope=perMachine` 的 MSI 无法实装**，本轮以真实窗口 UIA 点击（7 个标签页、检索、诊断刷新）补充了交互级证据 | 安装器实装与卸载清理未在真机执行 | 用管理员会话运行 MSI 安装→升级→卸载；macOS `sudo installer -pkg ... -target /` 后按 `UPGRADE-AND-UNINSTALL.md` 核验 |
| `S9-R4` | S9 供应链 | 离线 SBOM 与 `SHA256SUMS` 已实现并本地验证；CI 因账单未运行（2026-11-22 复核 `gh run view 44998908076`：`The job was not started because recent account payments have failed or your spending limit needs to be increased`），Release 附件未实际生成 | 供应链证据尚未随发布物提供 | 恢复 GitHub 账单/支出限额后运行 SBOM 作业与 `release.yml`，确认 `bom.json` 与 `SHA256SUMS` 出现在 Release |

---

## 6. 未完成项执行顺序（2026-11-22 更新）

代码与自动化测试已全部收口，剩余项都依赖特定环境。按“可在当前机器立即产出成果”优先排序：

| 顺序 | 项 | 依赖 | 产出判定 |
|---|---|---|---|
| 1 | `S9-R4` Release 与 SBOM 附件 | GitHub 账单/支出限额恢复（2026-11-22 复核：`recent account payments have failed or your spending limit needs to be increased`） | `bom.json` + `SHA256SUMS` + 三平台安装包出现在 Release |
| 2 | `S9-R1` + `S3-R1` 签名/公证/TCC | Windows 代码签名证书、Apple Developer ID、人工授权 | `signtool verify` / `spctl` / 公证票据输出；撤销一次授权后复测 |
| 3 | `S9-R3` 安装升级卸载与人工走查 | 管理员会话（MSI 为 `perMachine`）、三平台真机 | 安装→升级→卸载记录、数据保留核验、无障碍签字 |
| 4 | `S4-R` / `S7-R1` 黄金集 | 外挂 Paraformer/RapidOCR/Whisper/KWS/TTS 模型资产 + 用户自录唤醒词 | 噪声/口音 CER、真实截图 OCR、真人唤醒率与误唤醒率、TTS 音质 |
| 5 | `S5-R1` 候选模型档位 | 其它候选模型与同机内存曲线 | 轻量档/质量档选型结论 |
| 6 | `S9-R2` 热插拔 / `S3-R2` | 物理显示器/麦克风、KDE Wayland 与 X11 真机 | 插拔后采集恢复记录、KDE/X11/多显示器能力矩阵签字 |
| 7 | `S6-R1` 企业端点联调 | 企业网络与公网沙箱 | mTLS/CA/代理/断网回退联调记录 |

> 2026-11-22 已关闭：`S9-R2` 的 90 分钟真实浸泡与内存曲线、`S5-R1` 的局域网模型复测、`S9.29` 真实 UI 全流程、`S9.30` 图表/文案缺陷修复。
> `S5-R2` 跟随 `S9-R2` 的调度段执行；`S6-R2`、`S7-R3`、`S8-R*` 已关闭，仅在需要时做调优复跑。
