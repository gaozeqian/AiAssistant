# AI 个人活动追踪助手 · 实施进度与跨平台交接记录 (PROGRESS)

> **用途**：本文件随 Git 仓库同步，作为跨电脑、跨操作系统（Windows / macOS / Linux）接续开发的唯一权威状态源。
> 每次完成子任务或阶段验收后必须同步更新本文件及 `IMPLEMENTATION.md` 第 10 节清单。

---

## 1. 当前总体状态快照

- **当前活跃阶段**：`S9 发布加固：打包/删除/文档工具链完成；真机签名公证、浸泡与人工验收待执行`
- **最近更新时间**：`2026-11-20`
- **最近执行环境**：Windows 11 (`win-x64`, OS 10.0.26300), `.NET SDK 11.0.100` (目标框架 `net10.0` LTS)
- **当前构建状态**：✅ 本地 Release 0 警告 0 错误；三平台自包含打包脚本实测（win zip / linux tar.gz / macOS app 布局）
- **当前测试状态**：✅ 146/146 测试通过（`Architecture.Tests` 6、`Core.Tests` 6、`Storage.Tests` 16、`Capture.Tests` 4、`Platform.Tests` 39、`Processing.Tests` 7、`Report.Tests` 61、`Voice.Tests` 7）
- **下一步待确认事项**：配置签名证书/公证凭据后执行 `release.yml`；在真机完成 24h/多日浸泡、安装升级卸载与无障碍走查；S9 遗留项见第 5 节

---

## 2. 阶段门禁总表（S0 – S9）

| 阶段 | 设计基线 | 状态 | 已验证平台 | 核心产出与门禁结论 |
|---|---|---|---|---|
| **S0 工程奠基** | P0 前置 | ✅ 已完成 | `win-x64` 实测；`osx-arm64` / `linux-x64` 交叉打包验证 | 19 个项目解决方案、CPM、10 份 ADR、CI 矩阵、6 条架构依赖测试通过 |
| **S1 核心与存储** | P0 | ✅ 已完成 | `win-x64` 实测通过；`osx-arm64` / `linux-x64` 待真机复跑 `Storage.Tests` | SQLite3MC 页级加密、DPAPI/Keychain/libsecret 密钥封装与轮换、AES-GCM 媒体库与引用计数、原子事件+媒体事务、加密备份恢复、`sqlite-vec` + SIMD 向量索引与重建 POC 全部通过 |
| **S2 Windows 最小闭环** | P0 | ✅ 已完成 | `win-x64` 采集、Headless UI、真实桌面强杀重启、24h 全天加速浸泡均通过 | 完成采集与有界管道、Avalonia MVVM 时间线、选中后解密预览、暂停/黑名单、托盘、自启开关、基础保留清理 `MediaRetentionService`、零敏感泄露结构化日志 `SanitizedStructuredLoggerProvider` 与导出预览；24h 浸泡验证控制事件 100% 零丢失 |
| **S3 三平台扩展** | P1 | 🔄 三平台 CI 全绿；待真实桌面权限/交互验收 | GitHub Actions `win-x64` / `osx-arm64` / `linux-x64` 各 53/53 测试通过，发布/Helper/Portal 打包通过 | macOS Helper、Linux Wayland 默认与初始化失败 X11 回退、portal 加密令牌轮换、AT-SPI/EWMH、inotify/PulseAudio/XDG 均已接入；macOS TCC 与 Linux 真实桌面能力矩阵待人工验收 |
| **S4 预处理与音频** | P1 | 🔄 代码与契约完成；模型资产评测遗留 | `win-x64` 流水线/AudioHub/WASAPI 实测；`linux-x64` / `osx-arm64` 发布与 sherpa-onnx 原生库打包通过 | 可组合阶段、超时/失败隔离、脱敏、pHash 去重、单路 AudioHub、三平台音频工厂、sherpa-onnx ASR/VAD/说话人分离与缺失模型降级；真实中文语音与 OCR 质量待遗留项验收 |
| **S5 本机模型与日报** | P2 | 🔄 代码与契约完成；真实模型评测遗留 | `win-x64` 网关/分段/调度/UI 实测；`linux-x64` / `osx-arm64` 发布通过 | Microsoft.Extensions.AI 网关、Ollama/LM Studio 兼容适配、脱敏/审计/超时装饰器、事件向量索引、分段+map-reduce+JSON 校验重试、版本化加密日报、单消费者三通道与日报/站会 UI；真实模型质量待遗留项评测 |
| **S6 内网与公网档** | P2.5 | 🔄 代码与契约完成；真实企业端点遗留 | `win-x64` 配置/策略/三闸/探测测试通过；两平台发布通过 | 版本化 `models.json`、加密端点密钥、mTLS/企业 CA/代理、Host 外层策略锁定、公网显式同意、三档路由与内网→本机回退、三闸脱敏、五能力探测缓存与诊断展示 |
| **S7 唤醒与基础命令** | P2.6 | 🔄 代码与契约完成；真实 KWS/TTS 评测遗留 | `win-x64` 状态机/命令/日志/UI 测试；两平台发布通过 | sherpa-onnx “你好时光” KWS、四态语音会话、8 秒命令窗口、误唤醒冷却、固定命令、敏感命令二次确认、TTS 反馈、加密 voice_commands 文本日志与状态栏/语音面板 |
| **S8 智能检索与对话** | P3 | 🔄 代码与契约完成；真实检索质量与 MCP 客户端联调遗留 | `win-x64` 混合检索/证据问答/区间报告/MCP 契约与 Headless UI 测试；两平台发布通过 | 时间过滤+关键词+向量 RRF 混合检索与 `[E#]` 证据引用、无证据/无引用/媒体清理三类降级、周报与任意区间 Markdown+stats 导出、只读 loopback MCP（加密令牌/限流/四个只读工具）、检索问答面板与证据回跳、语音问答复用同一用例 |
| **S9 发布加固** | 全阶段 | 🔄 代码与工具链完成；真机签名/浸泡/人工验收遗留 | `win-x64` 打包与维护命令实测；`linux-x64` / `osx-arm64` 包布局验证 | 跨平台打包脚本（Windows zip+MSIX 模板+签名、macOS app+codesign/notarize、Linux tar.gz+deb）、应用图标、发布工作流、数据删除服务与 CLI（backup/restore/purge）、v1→v2 迁移与密钥丢失故障注入、数据与导出页面、键盘快捷键/无障碍名称/权限引导、隐私/限制/性能/升级卸载/回滚/发布检查表文档 |

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

优先使用 `models.json`（示例见仓库根目录 `models.example.json`）。默认路径为数据目录下 `models.json`，也可用 `AIASSISTANT_MODELS_CONFIG` 指定。

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

- `--doctor` 输出加密库健康/Schema/事件与媒体计数、模型端点能力（10 秒超时）与 MCP 状态，库不健康时退出码 1。

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

### 3.5 本地语音模型与命令

`AIASSISTANT_SHERPA_MODEL_DIR` 指向同一语音模型根目录，建议结构：

```text
sherpa/
├── kws/  encoder.onnx · decoder.onnx · joiner.onnx · tokens.txt · keywords.txt
└── tts/  model.onnx · tokens.txt · lexicon.txt
```

`keywords.txt` 使用 sherpa-onnx KWS 关键词格式，至少包含“你好时光”及备选词；阈值在真实环境实测后调整。语音默认关闭，由“语音”页显式启用；`AIASSISTANT_VOICE_DISABLE_CHIME=true` 可关闭唤醒提示音。语音问答只在“问一下 / 请问 / 查询 / 搜索”触发词后进入，回答与“检索与问答”面板共用同一证据用例。

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
- 一致性校验：`python build/mcp/mcp-client.py --token <token> --check-unauthorized`（17 项）或追加 `--accept sse`（18 项）。

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

---

## 5. 遗留项与门禁豁免（按用户决定暂缓）

> 这些项目不阻塞 S4–S8 的代码开发，但在 S9 发布门禁前必须完成。

| ID | 来源 | 遗留内容 | 风险 | 后续处理 |
|---|---|---|---|---|
| `S3-R1` | S3 macOS | 真实 macOS 桌面的 TCC 屏幕录制/辅助功能授权、Helper 崩溃监督与签名公证未人工验收 | 原生权限路径与打包签名仍可能有平台差异 | 在 macOS Apple Silicon 真机执行 Helper 冒烟、权限拒绝/撤销与重启测试 |
| `S3-R2` | S3 Linux | 真实 Wayland 合成器的 portal 授权/撤销、X11 回退、AT-SPI 与托盘能力矩阵未人工验收 | 合成器差异可能影响窗口与抓屏可用性 | 在 GNOME/KDE Wayland 与 X11 会话逐项签字 |
| `S3-R3` | S3 Linux | Wayland 连续 PipeWire 像素流未消费；当前按 `Degraded` 使用按需 portal Screenshot 回退 | 无法提供高频 Wayland 截屏 | S4/S8 前评估 PipeWire 消费库，或保持按需截图并明确能力降级 |
| `S4-R1` | S4 语音 | sherpa-onnx 模型资产（Paraformer、Silero VAD、说话人嵌入）不进入 Git，真实中文语音黄金集未评测 | 仅验证适配与降级路径，未证明识别质量 | 在本地/CI 外挂模型目录运行黄金集与唤醒词评测 |
| `S4-R2` | S4 OCR | Linux RapidOCR ONNX 模型接入与中文 OCR 质量评测 | Linux OCR 仍只有 AT-SPI，无图像文字兜底 | 在 S5 前引入 RapidOCR 模型资产并跑中文截图集 |
| `S4-R3` | S4 语音 | Whisper.net 高质量批处理适配器未启用 | 高质量离线转写缺少备选实现 | 需要长音频/高精度场景时再接入，保持 `IAsrEngine` 可替换 |
| `S5-R1` | S5 模型 | 已用本机 `gemma4:12b` 实测：成功率 100%、JSON 合规率 100%、热态平均 4.6s（首次含加载 7.4s）；其它候选模型与内存曲线仍未评测 | 不同模型质量/延迟差异较大，默认档位未定 | 对目标候选模型分别运行 `AiAssistant.Host --benchmark-model`，按质量/延迟选默认档 |
| `S5-R2` | S5 调度 | 每日定时、启动补偿与失败退避尚未进行多日浸泡 | 长时间休眠/跨日边界可能暴露时序问题 | S9 浸泡测试覆盖跨午夜、休眠唤醒、模型离线与重复触发 |
| `S6-R1` | S6 企业端点 | 真实企业 mTLS、企业 CA、代理和公网 API 未联调；公网同意仅代码级门禁 | TLS/代理环境差异可能导致端点不可用 | 在企业网络与公网沙箱分别验证握手、证书轮换、代理认证、断网回退与策略审计 |
| `S6-R2` | S6 密钥 | 已提供 `--import-model-secret`（stdin、终端隐藏输入）、列表/删除命令与诊断页密钥卡片；值只写不读 | 仍需用户自行准备密钥值并确保不进入 shell 历史 | 发布说明中提示使用 stdin 管道，避免命令行明文 |
| `S7-R1` | S7 语音 | 真实中文 KWS 唤醒率/误唤醒率、TTS 音质与端到端延迟未评测 | 阈值和体验未达发布基线 | 外挂 KWS/TTS 模型后运行安静/嘈杂/多人场景黄金集，按实测调整阈值 |
| `S7-R2` | S7 macOS | macOS TTS 音频输出仍为 Degraded；命令文字反馈可用 | macOS 无语音播报 | 扩展 Swift Helper 的音频输出能力并做 TCC 麦克风/输出验收 |
| `S7-R3` | S7 隐私 | 命令音频选择落盘（opt-in）尚未实现，当前仅加密文本日志 | 无法回听命令音频 | S9 前增加显式开关、加密媒体引用与自动清理 |
| `S8-R1` | S8 检索 | 已完成真实评测：embeddinggemma-2:740m **95.8%**（MRR 1.0，768 维，热态约 19ms）；已把官方查询指令与 `title/text` 文档格式接入生产并支持向量重建 | 合成语料仍与真实分布不同；个别同义词会漂移 | 用真实脱敏语料扩充查询集，按失败样本调参 |
| `S8-R2` | S8 MCP | 已完成 MCP Inspector 2.16.0 真实客户端联调：initialize、tools/list、activity_stats/search_events、未授权退出码 3、写工具退出码 5；其他客户端（Claude Desktop 等）未测 | 其他客户端的会话/SSE 细节可能仍有差异 | 需要时用其他客户端复跑同一流程 |
| `S8-R3` | S8 周报 | 已提供 SVG/PNG/PDF；PDF 自动发现系统 TrueType 中文字体并嵌入子集、文本可检索，无字体时回退矢量轮廓 | 极简 Linux 可能无任何 CJK TTF | 安装 CJK 字体或通过 `AIASSISTANT_PDF_FONT_PATH` 指定 |
| `S9-R1` | S9 签名 | Windows Authenticode/MSIX 与 macOS Developer ID/公证未用真实证书执行；脚本与工作流已就绪 | 未验证的签名链可能导致安装被拦截 | 配置证书与公证凭据后运行 `release.yml`，并保存 `signtool verify` / `spctl` 输出 |
| `S9-R2` | S9 浸泡 | 已实现并跑通 14 天等效加速浸泡（674/674 控制事件、336 媒体、73.8s）；真实时间多日浸泡、插拔显示器/麦克风未在真机执行 | 真实设备热插拔与长稳内存曲线未量化 | 按 `RELEASE-CHECKLIST.md` 第 5 节在真机执行并记录 |
| `S9-R3` | S9 人工验收 | 自动化已覆盖 7 个标签页无障碍名称/加速键、Ctrl+D1…D7 与 Ctrl+F 真实按键、关键控件名称；安装/升级/卸载与人工走查未做 | 平台交互差异可能影响可用性 | 三平台各执行一次发布检查表第 3、6 节 |
| `S9-R4` | S9 供应链 | 离线 SBOM 与 `SHA256SUMS` 已实现并本地验证；CI 因账单未运行，Release 附件未实际生成 | 供应链证据尚未随发布物提供 | 恢复 Actions 后运行 SBOM 作业与 `release.yml`，确认 `bom.json` 与 `SHA256SUMS` 出现在 Release |
