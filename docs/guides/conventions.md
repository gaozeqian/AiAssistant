# 目录与文档规范（含豁免表）

> **状态**：living ｜ **最后更新**：2026-11-22 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：目录/命名/文档规范变更，或新增、撤销豁免时

本文件是仓库工程规范的**唯一权威**：目录职责、命名、文档信息架构、单一事实源，以及无法完全合规项的豁免登记。审计脚本 `tools/audit-conventions.sh` 以本文件为基准。

## 1. 目录职责（R1 / R2）

根目录白名单：`README.md`、`LICENSE`、`EULA.md`、`CHANGELOG.md`、`CONTRIBUTING.md`、`SECURITY.md`、`THIRD-PARTY-NOTICES.md`、`.gitignore`、`.gitattributes`、`.editorconfig`、`.workbuddy/`、`.workbuddy-ai/`、`AiAssistant.sln`、`Directory.Build.props`、`Directory.Build.targets`、`Directory.Packages.props`、`global.json`、`dotnet.config`、`testconfig.json`、`nuget.config`、`.config/`、`src/`、`tests/`、`docs/`、`build/`、`tools/`、`native/`、`samples/`、`artifacts/`、`.github/`。

| 目录 | 唯一职责 | 禁止 |
|---|---|---|
| `src/` | 产品代码与工程 | 脚本、示例配置、文档 |
| `tests/` | 测试代码与测试资产 | 原生工程源码、构建脚本 |
| `docs/` | 文档 | 可执行脚本、产物 |
| `build/` | 构建、打包、签名、SBOM | 开发辅助客户端、日常运维脚本 |
| `tools/` | 审计、清理、冒烟、迁移、联调 | 构建流水线必需脚本 |
| `native/` | 原生工程（Swift） | .NET 代码 |
| `samples/` | 示例配置 | 真实密钥、生产配置 |
| `artifacts/` | 全部产物（不入库） | 源码、配置、文档 |
| `.github/` | CI 与社区模板 | 项目脚本 |

## 2. 命名规范（R3）

| 类别 | 规则 | 正则 | 反例 |
|---|---|---|---|
| 目录 | 小写 + 连字符 | `^[a-z0-9]+(-[a-z0-9]+)*$` | `docs/Release/` |
| 文档 | 小写 + 连字符；ADR `NNNN-kebab.md` | 同上 + `^\d{4}-` | `MODELS.md` |
| 工程/命名空间 | PascalCase；macOS 例外 | `^AiAssistant(\.[A-Z][A-Za-z0-9]*)+$` | `AiAssistant.Platform.MacOS` |
| 脚本 | kebab-case | `^[a-z0-9]+(-[a-z0-9]+)*\.(sh\|ps1)$` | `build.sh` |

平台名称唯一书写 `macOS`。允许的例外：.NET RID `osx-arm64` / `osx-x64`、Whisper 运行目录 `macos-arm64` / `macos-x64`、BCL API `OperatingSystem.IsMacOS()`、Apple bundle 布局 `Contents/MacOS`、Swift 包 `AiAssistant.MacHelper`。

## 3. 文档规范（R4）

- 入口唯一：`README.md`（仓库）与 `docs/README.md`（文档索引），任何文档 ≤2 跳可达。
- 每篇文档带四段头信息：`状态`（living / snapshot / archived）、`最后更新`、`适用版本`、`维护触发条件`。
- 相对链接，断链必须为 0。
- 正文中文，标识符与命令英文。

## 4. 单一事实源

同一事实只在一处定义，其余用链接引用。

| 主题 | 唯一权威 |
|---|---|
| 端口 / 环境变量 | `docs/reference/environment-variables.md` |
| 配置键 / 默认模型 / 数据目录 | `docs/reference/configuration.md` |
| CLI 参数 | `docs/reference/cli.md` |
| 产物路径与保留策略 | `docs/guides/artifacts.md` |
| 门禁状态 | `docs/status/status.md` |
| 遗留项 | `docs/status/backlog.md` |
| 模型许可 | `docs/compliance/model-assets.md` |
| 依赖许可 | `docs/compliance/dependency-manifest.md` |

## 5. 脚本规范（R6）

统一骨架（`set -euo pipefail`、`git rev-parse --show-toplevel` 解析仓库根、`--help`、破坏性操作 `--dry-run`、退出码 `0/1/2`），`.sh` 权限 755，`.ps1` UTF-8 无 BOM。完整模板见 [build/README.md](../../build/README.md)。

## 6. 豁免表（R10）

未在下方登记的违规视为未完成。新增豁免需说明理由与复审条件，并经维护者确认。

| 规则 | 位置 | 现状 | 理由 | 复审条件 | 批准人 |
|---|---|---|---|---|---|
| R1 | 根目录 | 增补 `samples/` 进白名单 | R9 要求示例配置放 `samples/`，白名单需同步 | 规范文本修订即可 | 维护者（2026-11-22） |
| R3 | 6 个文件 | `OperatingSystem.IsMacOS()` 12 处保留 `MacOS` | .NET BCL API，不可改 | .NET 提供新 API 时 | 维护者 |
| R3 | 打包脚本/AppRuntime | `Contents/MacOS` 13 处保留 | Apple bundle 目录规范 | Apple 变更布局时 | 维护者 |
| R3 | `native/AiAssistant.MacHelper` | 包名保留 `Mac` | Swift 包派生命名 | 包重命名时 | 维护者 |
| R2 | `src/` | 16 工程保持扁平 | 分组引入无收益命名空间动荡 | 工程数 > 25 时 | 维护者 |
| R8 | 根目录 | 不建 `CODE_OF_CONDUCT.md` | 非开源协作场景 | 对外招募贡献者时 | 维护者 |
| R5 | `build/package/*.ps1`、`generate-sbom.py` | `"artifacts"` 字面量各 1 处 | 各自语言的路径解析点，集中在 3 处 | 引入跨语言统一配置时 | 维护者 |
| R4 | 历史日志 | 归档文档与 `docs/status/log.md` 保留旧路径/旧类名叙述 | 历史记录不改写 | 无需 | 维护者 |
