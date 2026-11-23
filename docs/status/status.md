# 当前状态与阶段门禁

> **状态**：living ｜ **最后更新**：2026-11-22 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：每次阶段验收、门禁状态变化或发布前

本文件是**门禁状态的唯一权威**。实施日志见 [log.md](log.md)，遗留项与执行顺序见 [backlog.md](backlog.md)，规范合规结论见 [conformance.md](conformance.md)。

## 1. 当前总体状态

- **当前活跃阶段**：`S9 发布加固`：`win-x64` 已完成真实 UI 全流程与 90 分钟浸泡；剩余为真机签名/公证、管理员安装卸载、热插拔、模型资产与真实企业端点等门禁
- **最近更新时间**：`2026-11-22`
- **最近执行环境**：`win-x64` Windows 11（10.0.26300）+ `.NET SDK 10.0.402` + 局域网模型 `http://192.168.1.109:11434`（`gemma4:12b-mlx` / `embeddinggemma-2:740m`）；上一轮 `osx-arm64` macOS 26.7.3 + SDK 10.0.404；再上一轮 `linux-x64` Ubuntu 26.04 + GNOME Wayland
- **当前构建状态**：✅ `win-x64` 本轮 Release 0 警告 0 错误（Host/Desktop/Report.Tests 实建）；`osx-arm64` Swift Helper 历史结论见第 4 节日志
- **当前测试状态**：✅ 2026-11-23 起 `dotnet test --solution AiAssistant.sln -c Release` 在 Microsoft.Testing.Platform 原生模式下全量通过（**231/231**），旧 VSTest 回环 Socket 崩溃已根除；`dotnet format --verify-no-changes` 与 `dotnet build --warnaserror` 均为 0 差异 / 0 警告 0 错误。Swift Helper `swift build -c release --disable-sandbox` 构建通过。历史 `--ui-e2e` 真机结论见归档日志；该自驱动流程已迁至工具宿主，不随产品发布。
- **下一步待确认事项**：`S9-R4` 需先恢复 GitHub 账单/支出限额；`S9-R1`/`S3-R1` 需真实证书与 macOS Developer ID；`S9-R3` 需管理员会话实装 MSI 并做人工走查签字；`S3-R2` 需 KDE Wayland/X11 真机；`S4-R`/`S7-R1` 需外挂语音/OCR 模型与真人唤醒词正样本；`S9-R2` 仅剩显示器/麦克风热插拔。详见 [backlog.md](backlog.md)

---

## 2. 阶段门禁总表

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
| **S9 发布加固** | 全阶段 | 🔄 代码与工具链完成；`win-x64` 90 分钟浸泡与真实 UI 全流程通过；真机签名/热插拔/安装卸载遗留 | `win-x64` 打包与维护命令实测、真实窗口 UI E2E 17/17×3、90 分钟真实浸泡通过；`linux-x64` / `osx-arm64` 包布局验证 | 跨平台打包脚本（Windows zip+MSI、macOS app+codesign/notarize、Linux tar.gz+deb）、应用图标、发布工作流、数据删除服务与 CLI（backup/restore/purge）、v1→v2 迁移与密钥丢失故障注入、数据与导出页面、键盘快捷键/无障碍名称/权限引导、隐私/限制/性能/升级卸载/回滚/发布检查表文档 |

---

## 3. 相关文档

| 主题 | 位置 |
|---|---|
| 实施日志（时间倒序） | [log.md](log.md) |
| 遗留项与执行顺序 | [backlog.md](backlog.md) |
| 规范合规矩阵 | [conformance.md](conformance.md) |
| 发布检查表 | [../release/release-checklist.md](../release/release-checklist.md) |
