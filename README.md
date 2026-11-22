# AiAssistant（时光）

> **专有软件 / Proprietary.** 本仓库为闭源商业软件，未经书面授权不得复制、分发、修改或反向工程。
> 使用条款见 [LICENSE](LICENSE) 与 [EULA.md](EULA.md)；第三方组件归属见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

本机运行的个人活动追踪与日报助手：采集前台窗口、白名单目录的文件活动与屏幕截图，用**本机模型**汇总成日报。采集数据、模型推理与导出文件都不离开本机。

## 能力

| 平台 | 交付格式 | 验证状态 |
|---|---|---|
| Windows x64 | `zip` + `MSI` | 打包、UI 全流程、90 分钟浸泡实测 |
| macOS arm64 | `.pkg` + `.zip` | 包布局实测；签名与公证脚本就绪，需真实 Developer ID |
| Linux x64 | `tar.gz` + `.deb` | 包布局实测；Wayland 走 xdg-desktop-portal，可回退 X11 |

- **采集**：前台窗口与焦点、文件活动（目录白名单 + 扩展名/体积上限）、按需与连续两档截图。
- **存储**：SQLite3MC 加密库、系统密钥库、媒体 AES-GCM、`sqlite-vec` 向量索引（含降级兜底）。
- **汇总**：定时 / 空闲 / 关机补偿三段触发，日报版本化保留历史。
- **模型**：三档端点（本机 Ollama / 内网 / 企业公网），默认对话 `gemma4:12b`、embedding `embeddinggemma-2:740m`。
- **语音**：唤醒词「你好时光」→ Paraformer ASR → 指令执行；MeloTTS 播报。
- **导出**：Markdown / SVG / PNG / PDF 区间报告 + stats JSON。

## 5 分钟跑起来

```bash
dotnet tool restore                      # WiX（仅 Windows 打包需要）
dotnet restore AiAssistant.sln
dotnet build AiAssistant.sln -c Release
dotnet test  AiAssistant.sln -c Release --no-build
dotnet run --project src/AiAssistant.Host/AiAssistant.Host.csproj
```

前置条件：.NET SDK 10.0.100+（`global.json` 锁定，允许向上滚动）。本机模型需先装好 Ollama 并拉取默认模型，或用 `models.json` 指向内网端点。

自检与维护命令：

```bash
AiAssistant.Host --doctor                 # 环境与依赖自检
AiAssistant.Host --smoke-check            # 无 UI 冒烟
AiAssistant.Host --report-range --from 2026-11-16 --to 2026-11-22
```

## 目录导览

| 目录 | 职责 |
|---|---|
| `src/` | 产品代码（16 个工程，扁平布局） |
| `tests/` | 单元、集成、契约与架构测试 |
| `docs/` | 全部文档，索引见 `docs/README.md` |
| `build/` | 构建、打包、签名、SBOM |
| `tools/` | 审计、清理与联调工具 |
| `native/` | macOS Swift Helper |
| `samples/` | 示例配置（`models.example.json`） |
| `artifacts/` | 全部产物，不入库 |

## 文档

| 主题 | 位置 |
|---|---|
| 本机模型与语音模型安装 | [docs/MODELS.md](docs/MODELS.md) |
| 设计方案 | [docs/architecture/design.md](docs/architecture/design.md) |
| 实施方案 | [docs/architecture/implementation.md](docs/architecture/implementation.md) |
| 当前状态与门禁 | [docs/status/progress.md](docs/status/progress.md) |
| 架构决策记录 | [docs/adr/](docs/adr/) |
| 依赖与模型许可 | [docs/compliance/](docs/compliance/) |
| 发布、升级与回滚 | [docs/release/](docs/release/) |

## 当前状态

版本 `0.1.0`。代码与自动化测试已收口；剩余项依赖真实证书、真机与 GitHub 账单，逐条列在 `docs/status/progress.md` 第 5、6 节。

## 隐私与许可

- 隐私说明：[docs/release/PRIVACY.md](docs/release/PRIVACY.md)
- 许可：[LICENSE](LICENSE) · [EULA.md](EULA.md)
- 第三方组件：[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
- 安全问题请按 [SECURITY.md](SECURITY.md) 私下报告。
