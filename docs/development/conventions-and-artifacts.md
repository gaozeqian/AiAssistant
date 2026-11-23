# 开发规范与产物目录

> **状态**：living ｜ **最后更新**：2026-11-23 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：目录职责、产物布局、命名或审计规则变化时

本文件合并原 `guides/conventions.md` 与 `guides/artifacts.md`，是目录、产物与命名规范的唯一事实源。

## 1. 根目录职责

| 路径 | 职责 |
|---|---|
| `src/` | 16 个产品工程；不放脚本、示例、测试或文档 |
| `tests/` | 测试工程与共享测试基建；临时文件只写 `artifacts/tmp/` |
| `tools/` | 人手动跑的工具：诊断宿主、审计、清理、模型安装、MCP 联调、macOS UI E2E |
| `build/` | 构建流水线必需脚本：模型、打包、SBOM、Swift 构建入口 |
| `native/` | Swift Helper 源码与 `Package.swift`；无编译产物入库 |
| `docs/` | 文档；唯一入口 [docs/README.md](../README.md) |
| `samples/` | 可提交的示例配置 |
| `artifacts/` | 唯一产物根（不入库） |

根目录白名单由 `tools/audit-conventions.sh` 检查；新增根条目必须先更新白名单与本文。

## 2. 零源码树污染

以下目录严禁出现在 `src/ tests/ build/ tools/ native/ docs/`：

- SwiftPM `.build/`、`.swiftpm/`
- Python `__pycache__/`
- 测试 `test-temp/`
- `bin/`、`obj/`、`TestResults/`
- 嵌套 `artifacts/`
- 仓库根临时 `dist/`

SwiftPM 统一经 `build/native/mac-helper.sh` 使用 `--scratch-path artifacts/obj/native/mac-helper`；测试统一经 `TempArtifactDirectory` 使用 `artifacts/tmp/<suite>/<guid>`。

## 3. `artifacts/` 目录契约

| 目录 | 内容 | 保留策略 |
|---|---|---|
| `bin/` | `dotnet build` 输出 | 可随时删除 |
| `obj/` | restore/编译中间产物；`obj/native/mac-helper/` 为 SwiftPM scratch | 可随时删除 |
| `publish/<rid>/` | `dotnet publish` 唯一输出 + `publish-manifest.txt` | 可随时删除 |
| `staging/<rid>-<format>/` | `app` / `pkg` / `deb` / `msi` / `portable` 组装区 | 可随时删除 |
| `dist/<version>/` | 最终安装包、便携包、SBOM、校验文件 | 保留最新版本 |
| `cache/models/` | 外部模型下载缓存与 `bundle/` 随包载荷；`_retired/` 永久豁免 | 仅 `--models` 清理 |
| `test-results/` | MTP 结果、`coverage/`、`e2e/`、`benchmarks/`、`soak/`、`golden/`、`retired-models/` | E2E 保留 5 轮；其余按需 |
| `test/` | `dotnet test` 生成的测试宿主工作目录（SDK 行为） | 可随时删除 |
| `tmp/` | 测试/脚本临时工作区 | 可随时删除 |

`ArtifactPaths` 是唯一解析入口；代码与脚本禁止硬编码 `"artifacts"` 路径（审计 R5）。默认根从 `AppContext.BaseDirectory` 与 CWD 向上探测 `AiAssistant.sln`，避免子目录运行生成嵌套目录；`AIASSISTANT_ARTIFACTS_ROOT` 可整体搬迁。

## 4. 发布态命名

| 平台 | 可执行文件 | 分发包 |
|---|---|---|
| Windows x64 | `AiAssistant.exe` | `AiAssistant-<ver>-windows-x64.msi`、`AiAssistant-<ver>-windows-x64-portable.zip` |
| macOS arm64 | `AiAssistant`（`.app/Contents/MacOS/`） | `AiAssistant-<ver>-macos-arm64.pkg`、`AiAssistant-<ver>-macos-arm64.app.zip` |
| Linux x64 | `aiassistant` | `aiassistant_<ver>_amd64.deb`、`aiassistant-<ver>-linux-x64.tar.gz`（顶层目录） |

- 开发自签名构建统一追加 `-dev`（如 `...-dev.msi`、`...-dev.app.zip`、`...-dev.cer`）。
- SBOM：`AiAssistant-<ver>-sbom.cdx.json`；单包 `<file>.sha256`；汇总 `AiAssistant-<ver>-SHA256SUMS.txt`。
- Windows 便携包与 MSI 共享 `artifacts/publish/win-x64`，禁止二次 publish。

## 5. 代码规范

- 内部实现类默认 `internal sealed`；公开面只保留端口、用例与必要的 UI 类型（架构测试 Rule10）。
- 生产代码禁止新增第三方 NuGet/Swift 依赖；包版本只在 `Directory.Packages.props` 维护。
- 环境变量只能通过 `EnvironmentNames` 常量引用。
- 时间只能通过 `IClock`；`TimeProvider.System` 仅允许出现在 `IClock` 实现。
- 空 `catch` 必须调用 `SuppressedExceptionLog.Trace` 脱敏记录。
- 架构与依赖由 `DependencyRuleTests` 强制；测试类必须声明 `Category` Trait，禁止静默 `return`。

## 6. 脚本与文档

- `.sh` 使用 `set -euo pipefail`、`git rev-parse --show-toplevel`、`--help`，破坏性操作默认 dry-run，权限位 755。
- Python 调用使用 `-B` 或 `PYTHONDONTWRITEBYTECODE=1`，避免 `__pycache__`。
- 文档使用小写连字符文件名，四段头：状态 / 最后更新 / 适用版本 / 维护触发条件；全仓文档从 [docs/README.md](../README.md) ≤2 跳可达。

## 7. 清理与审计

```bash
bash tools/clean-artifacts.sh --help
bash tools/clean-artifacts.sh --apply --yes            # bin/obj/publish/staging/tmp + 旧 dist
bash tools/clean-artifacts.sh --apply --yes --models   # 另清模型缓存（保留 _retired）
bash tools/audit-conventions.sh
```
