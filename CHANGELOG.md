# 变更记录

本文件记录 AiAssistant 的用户可见变更。格式遵循 [Keep a Changelog 1.1.0](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [SemVer 2.0.0](https://semver.org/lang/zh-CN/)。

版本号以仓库根 `Directory.Build.props` 的 `VersionPrefix` 为准，本文件首条必须与之一致。

## [Unreleased]

### Added

- 工程底座：统一 `artifacts/` 输出、`nuget.config` 源映射与全量 NuGet 审核、CycloneDX 1.6 SBOM（含 Swift helper 与本地模型 ML-BOM）。
- 测试栈：`xunit.v3` + Microsoft.Testing.Platform 原生运行器，根除 VSTest 回环 Socket 崩溃；全量测试在本机原生通过。
- 安全：本地 MCP 服务新增 `Origin` / `Host` 白名单校验、`MCP-Protocol-Version` 协商，并将鉴权严格前置于限流之前。
- 架构门禁：16 模块依赖矩阵、生产程序集禁引测试/基准类型、生产代码禁直接调用系统时钟的自动化断言。

### Changed

- 时钟访问统一收敛到 `IClock`（对齐 `System.TimeProvider`）；模型地址与默认模型名收敛到 `ModelDefaults`。
- E2E 自驱动与基准命令从生产交付剥离到 `tools/AiAssistant.Diagnostics.Host`。
- 三平台交付矩阵四处文档/CI 对齐：Windows `.zip` + `.msi`、macOS `.pkg` + `.zip`、Linux `.tar.gz` + `.deb`。
- 自签名根证书导入仅在显式 `--dev-self-signed` 模式下发生，正式 CA / Developer ID 路径物理隔离。
- CI 补齐最小权限、并发控制、作业超时、Action SHA 锁定与格式/ShellCheck 检查。

### Fixed

- `ModelHttpClientFactory` 中客户端证书与企业 CA 句柄随 Handler 释放，消除长期存活闭包导致的资源泄漏。
- 清理重复图标资产、UTF-8 BOM、阶段代号测试命名与 GNU 专属脚本命令。

## [0.1.0] - 2026-11-22

首版。三平台本机活动追踪与日报，全部推理在本机完成。

### Added

- 采集：前台窗口与焦点、用户白名单目录内的文件活动、屏幕截图（按需与连续两档），全部走平台适配器。
- 存储：SQLite3MC 加密数据库、系统密钥库托管的密钥、媒体文件 AES-GCM 加密、`sqlite-vec` 向量索引与降级兜底。
- 汇总：三段式触发（定时 / 空闲 / 关机补偿）生成日报，日报版本化并保留历史。
- 展示：Avalonia 桌面端（时间线、检索与问答、隐私与控制、语音、数据与导出、本地诊断、日报共七页），支持 macOS / Windows / Linux。
- 模型：统一 `IChatClient` 网关，支持本机 Ollama、内网端点与企业公网端点三档策略；默认对话模型 `gemma4:12b`，默认 embedding 模型 `embeddinggemma-2:740m`。
- 语音：sherpa-onnx 唤醒词 + Paraformer ASR + MeloTTS 合成 + Silero VAD，唤醒词「你好时光」。
- 导出：Markdown / SVG / PNG / PDF 四类区间报告，附 stats JSON。
- 集成：只读本地 MCP 服务（默认关闭）、`--doctor` 自检、备份 / 恢复 / 清空数据等维护命令。
- 打包：Windows `zip` + `MSI`、macOS `.pkg` + `.zip`、Linux `tar.gz` + `.deb`；安装包随带 KWS / Paraformer / MeloTTS / Silero VAD 四类模型资产。

### Changed

- 随包 TTS 由 `vits-zh-aishell3` 更换为 MeloTTS（MIT）；旧权重归档至 `artifacts/evidence/retired-models/`，不再参与打包。
- Windows 交付格式收敛为 `zip` 与 `MSI`，MSIX 打包链路下线。

### Security

- 随包资产许可收敛到 MIT / Apache-2.0 白名单；`THIRD-PARTY-NOTICES.md` 收录 Paraformer 三级归属链与四份许可证全文。
- 依赖许可门禁：仅允许 MIT / Apache-2.0 / BSD-2-Clause / BSD-3-Clause。
