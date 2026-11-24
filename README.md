# AiAssistant（时光）

> **专有软件 / Proprietary.** 本仓库为闭源商业软件，未经书面授权不得复制、分发、修改或反向工程。
> 使用条款见 [LICENSE](LICENSE) 与 [EULA.md](EULA.md)；第三方组件归属见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

本机运行的个人活动追踪与日报助手：采集前台窗口、白名单目录的文件活动与屏幕截图，用**本机模型**汇总成日报。采集数据、模型推理与导出文件都不离开本机；公网端点必须显式同意。

## 能力

| 平台 | 交付格式 | 可执行文件 |
|---|---|---|
| Windows x64 | `AiAssistant-<ver>-windows-x64-portable.zip` + `.msi` | `AiAssistant.exe` |
| macOS arm64 | `AiAssistant-<ver>-macos-arm64.app.zip` + `.pkg` | `AiAssistant` |
| Linux x64 | `aiassistant-<ver>-linux-x64.tar.gz` + `aiassistant_<ver>_amd64.deb` | `aiassistant` |

- **采集**：前台窗口与焦点、白名单目录文件活动、按需与连续两档截图；暂停后不补采。
- **存储**：SQLite3MC 加密库、系统密钥库、媒体 AES-GCM、`sqlite-vec` 向量索引与降级兜底。
- **汇总**：定时 / 空闲 / 关机补偿三段触发；日报版本化保留历史。
- **模型**：本机 / 内网 / 公网三档策略，统一走 OpenAI 兼容协议（默认对接本机 Ollama）；默认对话 `gemma4:12b`、embedding `embeddinggemma-2:740m`。
- **语音**：唤醒词「你好时光」→ Paraformer ASR → 指令执行；MeloTTS 播报。
- **导出**：Markdown / SVG / PNG / PDF 区间报告 + stats JSON。
- **本地 MCP**：默认关闭的只读 Loopback 端点，Bearer 令牌 + Origin/Host 白名单 + 限流。

## 5 分钟跑起来

```bash
dotnet tool restore
dotnet restore AiAssistant.sln
dotnet build AiAssistant.sln -c Release --warnaserror
dotnet test  --solution AiAssistant.sln -c Release

# 桌面端
dotnet run --project src/AiAssistant.Host/AiAssistant.Host.csproj

# 自检与维护（发布态二进制名为 AiAssistant）
dotnet run --project src/AiAssistant.Host/AiAssistant.Host.csproj -- --doctor
dotnet run --project src/AiAssistant.Host/AiAssistant.Host.csproj -- --smoke-check
dotnet run --project src/AiAssistant.Host/AiAssistant.Host.csproj -- --report-range --from 2026-11-16 --to 2026-11-22
```

前置：.NET SDK 10.0.x（`global.json` 锁定，允许向 11+ 前滚）。本机模型需先安装 Ollama 并拉取默认模型，或用 `models.json` 指向内网端点。完整环境与构建见 [开发文档](docs/development/setup-and-build.md)。

## 目录导览

| 目录 | 职责 |
|---|---|
| `src/` | 产品代码（16 个工程，扁平布局） |
| `tests/` | 15 个测试工程与共享测试基建 |
| `docs/` | 全部文档，索引见 [docs/README.md](docs/README.md) |
| `build/` | 构建、打包、签名、SBOM、Swift 构建入口 |
| `tools/` | 诊断宿主、审计、清理、模型安装、MCP 联调、macOS UI E2E |
| `native/` | macOS Swift Helper（源码与测试） |
| `samples/` | 示例配置（`models.example.json`） |
| `artifacts/` | 唯一产物根（不入库） |

所有构建、测试、打包与 SBOM 只写 `artifacts/`；源码树零 `bin/obj/.build/__pycache__/test-temp`。目录与命名规范见 [开发规范与产物](docs/development/conventions-and-artifacts.md)。

## 文档入口

| 主题 | 位置 |
|---|---|
| 全部文档索引 | [docs/README.md](docs/README.md) |
| 环境搭建与构建 | [docs/development/setup-and-build.md](docs/development/setup-and-build.md) |
| 架构总览 | [docs/architecture/overview.md](docs/architecture/overview.md) |
| 测试与评测 | [docs/development/testing-and-benchmarks.md](docs/development/testing-and-benchmarks.md) |
| 模型与语音 | [docs/guides/models.md](docs/guides/models.md) |
| CLI / 配置 / 环境变量 | [docs/reference/cli.md](docs/reference/cli.md) · [configuration.md](docs/reference/configuration.md) · [environment-variables.md](docs/reference/environment-variables.md) |
| 安全与隐私 | [docs/release/security-and-privacy.md](docs/release/security-and-privacy.md) |
| 合规与 SBOM | [docs/release/compliance.md](docs/release/compliance.md) |
| 发布、升级与回滚 | [docs/release/release-and-deployment.md](docs/release/release-and-deployment.md) |
| 路线图与当前状态 | [docs/release/roadmap.md](docs/release/roadmap.md) |

## 当前状态

版本 `0.1.0`。三平台构建、测试、打包与文档体系已收口；当前测试 253 例、0 失败，受限环境用例显式 Skip。剩余项（正式证书、macOS CI 真机、Intel 包等）见 [路线图](docs/release/roadmap.md)。

## 隐私与许可

- 安全与隐私：[docs/release/security-and-privacy.md](docs/release/security-and-privacy.md)
- 许可：[LICENSE](LICENSE) · [EULA.md](EULA.md)
- 第三方组件：[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
- 漏洞报告：[SECURITY.md](SECURITY.md)
