# AI 个人活动追踪助手 · 实施进度与跨平台交接记录 (PROGRESS)

> **用途**：本文件随 Git 仓库同步，作为跨电脑、跨操作系统（Windows / macOS / Linux）接续开发的唯一权威状态源。
> 每次完成子任务或阶段验收后必须同步更新本文件及 `IMPLEMENTATION.md` 第 10 节清单。

---

## 1. 当前总体状态快照

- **当前活跃阶段**：`S3.2 Linux Wayland 默认与 X11 回退`（S3.1 macOS IPC / Helper / 适配器已实现并通过 Windows 契约测试；macOS 原生编译与 TCC 权限待真机或 CI 验证）
- **最近更新时间**：`2026-11-20`
- **最近执行环境**：Windows 11 (`win-x64`, OS 10.0.26300), `.NET SDK 11.0.100` (目标框架 `net10.0` LTS)
- **当前构建状态**：✅ `dotnet build AiAssistant.sln -c Release` 0 警告 0 错误；`osx-arm64` 交叉发布通过
- **当前测试状态**：✅ 43/43 测试全部通过（`Architecture.Tests` 6、`Core.Tests` 6、`Storage.Tests` 10、`Capture.Tests` 3、`Platform.Tests` 18，含 6 个 macOS 真实 AF_UNIX IPC / 故障恢复契约测试）
- **下一步待确认事项**：启动 S3.2 —— 实现 Linux 原生 Wayland 默认与 X11 回退、xdg-desktop-portal 授权恢复、AT-SPI / EWMH、inotify / XDG 自启适配器与契约测试；S3.1 的 Swift 原生编译、TCC 弹窗与签名公证仍需 macOS 真机或 CI 复核

---

## 2. 阶段门禁总表（S0 – S9）

| 阶段 | 设计基线 | 状态 | 已验证平台 | 核心产出与门禁结论 |
|---|---|---|---|---|
| **S0 工程奠基** | P0 前置 | ✅ 已完成 | `win-x64` 实测；`osx-arm64` / `linux-x64` 交叉打包验证 | 19 个项目解决方案、CPM、10 份 ADR、CI 矩阵、6 条架构依赖测试通过 |
| **S1 核心与存储** | P0 | ✅ 已完成 | `win-x64` 实测通过；`osx-arm64` / `linux-x64` 待真机复跑 `Storage.Tests` | SQLite3MC 页级加密、DPAPI/Keychain/libsecret 密钥封装与轮换、AES-GCM 媒体库与引用计数、原子事件+媒体事务、加密备份恢复、`sqlite-vec` + SIMD 向量索引与重建 POC 全部通过 |
| **S2 Windows 最小闭环** | P0 | ✅ 已完成 | `win-x64` 采集、Headless UI、真实桌面强杀重启、24h 全天加速浸泡均通过 | 完成采集与有界管道、Avalonia MVVM 时间线、选中后解密预览、暂停/黑名单、托盘、自启开关、基础保留清理 `MediaRetentionService`、零敏感泄露结构化日志 `SanitizedStructuredLoggerProvider` 与导出预览；24h 浸泡验证控制事件 100% 零丢失 |
| **S3 三平台扩展** | P1 | 🔄 S3.1 代码与 Windows 契约测试完成；S3.2 待开始 | `win-x64` 实测 AF_UNIX IPC / 崩溃恢复；`osx-arm64` 交叉发布通过；macOS / Linux 原生桌面待真机或 CI 复核 | 已完成 macOS IPC v1、一次性鉴权、心跳超时、指数退避监督器、ScreenCaptureKit / AX / Vision / FSEvents / AVAudioEngine / LaunchAgent 与打包配置；待 Linux 适配器和原生平台验收 |
| **S4 预处理与音频** | P1 | ⏳ 待开始 | — | 可组合 `IProcessingStage`、单路 `AudioHub`、sherpa-onnx ASR/VAD |
| **S5 本机模型与日报** | P2 | ⏳ 待开始 | — | Microsoft.Extensions.AI 网关、Ollama、map-reduce、三通道日报 |
| **S6 内网与公网档** | P2.5 | ⏳ 待开始 | — | `models.json`、企业策略锁定、能力探测、脱敏三道闸、审计 |
| **S7 唤醒与基础命令** | P2.6 | ⏳ 待开始 | — | “你好时光” KWS、语音状态机、8s 命令窗口、TTS、固定命令 |
| **S8 智能检索与对话** | P3 | ⏳ 待开始 | — | 混合检索、证据回链问答、周报、只读 loopback MCP、完整语音对话 |
| **S9 发布加固** | 全阶段 | ⏳ 待开始 | — | 安装包签名公证、浸泡测试、故障注入、升级迁移、SBOM |

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
