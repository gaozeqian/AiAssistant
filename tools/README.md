# tools/ — 开发与运维工具

本目录放「人手动跑」的工具：诊断、审计、清理、冒烟、联调。构建流水线必需的脚本放 `build/`。

| 路径 | 用途 | 适用平台 |
|---|---|---|
| `AiAssistant.Diagnostics.Host/` | 开发者专用宿主：`--ui-e2e` 真实窗口自驱动，ASR/KWS/OCR/检索基准 | Windows / macOS / Linux |
| `audit-conventions.sh` | 仓库规范审计（R1–R10 及补充规则），CI 与本地共用 | POSIX（macOS / Linux / Git Bash） |
| `clean-artifacts.sh` | 按保留策略清理 `artifacts/`，默认 dry-run | POSIX |
| `install-voice-models.sh` | 把已下载的语音模型装到应用数据目录（按 OS 解析默认路径） | macOS / Linux |
| `install-desktop-entry.sh` | Linux 桌面入口与图标 | Linux |
| `mcp/` | 只读本地 MCP 的联调客户端（Python 一致性客户端 + Inspector 冒烟） | 全平台 |
| `ui-e2e/` | macOS 真实 `.app` 的 AX / OCR / CGEvent 驱动脚本 | macOS |

## 约定

- 与 `build/` 相同的脚本骨架：`set -euo pipefail`、`git rev-parse --show-toplevel` 解析仓库根、`--help`、破坏性操作 `--dry-run`。
- 工具不得被 `build/package/*` 或 CI 打包作业依赖；否则应移入 `build/`。
- 所有临时文件写入 `artifacts/tmp/`，SwiftPM 中间产物写入 `artifacts/obj/native/`；不得在源码树生成 `.build/`、`__pycache__/` 或嵌套 `artifacts/`。
- 新增工具需在本表登记一行，并注明适用平台。

## 常用

```bash
bash tools/audit-conventions.sh          # 规范审计
bash tools/clean-artifacts.sh --help     # 查看保留策略
bash tools/clean-artifacts.sh --apply    # 真正删除（按子目录确认）

# 基准（报告写入 artifacts/test-results/benchmarks）
dotnet run --project tools/AiAssistant.Diagnostics.Host -c Release -- --benchmark-retrieval

# 真实窗口自驱动（截图与报告写入 artifacts/test-results/e2e）
dotnet run --project tools/AiAssistant.Diagnostics.Host -c Release -- --ui-e2e
```
