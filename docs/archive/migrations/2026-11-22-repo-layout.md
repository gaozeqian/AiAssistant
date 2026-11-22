# 仓库布局重构迁移映射（2026-11-22）

> **状态**：archived ｜ **最后更新**：2026-11-22 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：不再维护

本次重构把仓库收敛到 R1–R10 规范。本文件记录旧路径 → 新路径的完整映射，便于追溯与回滚。

## 1. 根目录

| 旧 | 新 | 说明 |
|---|---|---|
| `DESIGN.md` | `docs/architecture/design.md` | 设计方案 |
| `IMPLEMENTATION.md` | `docs/architecture/implementation.md` | 实施方案 |
| `PROGRESS.md` | 拆分见 [2026-11-22-docs-split.md](2026-11-22-docs-split.md) | 状态 / 日志 / 遗留 / 运维 |
| `models.example.json` | `samples/models.example.json` | 示例配置 |
| `dotnet-tools.json` | `.config/dotnet-tools.json` | WiX 本地工具 |
| （缺） | `CHANGELOG.md` / `CONTRIBUTING.md` / `SECURITY.md` / `.gitattributes` | 新增 |
| （缺） | `.github/PULL_REQUEST_TEMPLATE.md`、`ISSUE_TEMPLATE/`、`CODEOWNERS`、`dependabot.yml` | 新增 |

## 2. 目录

| 旧 | 新 | 说明 |
|---|---|---|
| `build/mcp/` | `tools/mcp/` | 开发联调工具 |
| `build/install-desktop-entry.sh` | `tools/install-desktop-entry.sh` | 运维脚本 |
| `build/install-voice-models.sh` | `tools/install-voice-models.sh` | 运维脚本 |
| `src/AiAssistant.Platform.MacOS/` | `src/AiAssistant.Platform.macOS/` | 命名统一 |
| （缺） | `src/README.md`、`tests/README.md`、`build/README.md`、`tools/README.md`、`native/README.md`、`samples/README.md` | 职责声明 |

## 3. 产物 `artifacts/`

| 旧 | 新 |
|---|---|
| `artifacts/packages/` | `artifacts/package/` |
| `artifacts/packages/staging/` | `artifacts/build/staging/` |
| `artifacts/models/` | `artifacts/cache/models/` |
| `artifacts/models/_retired/` | `artifacts/evidence/retired-models/` |
| `artifacts/e2e/` | `artifacts/evidence/e2e/` |
| `artifacts/benchmarks/` | `artifacts/evidence/benchmarks/` |
| `artifacts/soak/` | `artifacts/evidence/soak/` |
| `artifacts/test-temp/` | `artifacts/tmp/` |
| `artifacts/packages-linux-check/` | `artifacts/evidence/packages/linux-check/` |
| `artifacts/packages-macos-check/` | `artifacts/evidence/packages/macos-check/` |
| `artifacts/sbom/` | `artifacts/build/sbom/` |

产物路径统一由 `AiAssistant.Core.Configuration.ArtifactPaths`（.NET）、`AIASSISTANT_ARTIFACTS_ROOT`（shell/PS/Python）解析。

## 4. 类型与符号重命名

| 旧 | 新 |
|---|---|
| `namespace AiAssistant.Platform.MacOS` | `namespace AiAssistant.Platform.macOS` |
| `MacOsScreenCapture` / `MacOsCaptureAdapters` 等 11 个类型 | 去掉 `MacOs` 前缀（命名空间已限定平台） |
| `PlatformMacOSAssemblyMarker` | `PlatformAssemblyMarker` |
| `MacOsIpcAndAdapterContractTests` / `MacOsLiveHelperTests` | `MacPlatform*Tests` |
| MSBuild 目标 `BundleMacOsSwiftHelper` | `BundleSwiftHelper` |
| `RegisterMacOS` | `RegisterMacPlatform` |
