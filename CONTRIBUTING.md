# 参与开发

AiAssistant 是**专有闭源**项目，本文件面向获得授权的内部开发者。未获授权者请勿提交代码、文档或问题报告；分发与修改受仓库根 `LICENSE` 与 `EULA.md` 约束。

## 1. 环境准备

| 依赖 | 版本 | 说明 |
|---|---|---|
| .NET SDK | 10.0.100+（`global.json` 锁定，`rollForward: latestMajor`） | 构建与测试 |
| Git | 2.30+ | 提交前请执行 `git config core.quotepath false`，否则中文文件名会显示为八进制转义 |
| Python | 3.10+ | 图标生成、SBOM 生成、`.deb` 打包 |
| Swift | 6.0+ | 仅 macOS：`native/AiAssistant.MacHelper` |
| WiX | 5.0.2 | 由 `dotnet tool restore` 安装，仅 Windows MSI 需要 |

```bash
dotnet tool restore
dotnet restore AiAssistant.sln
dotnet build AiAssistant.sln -c Release --warnaserror
dotnet format AiAssistant.sln --verify-no-changes
dotnet test --solution AiAssistant.sln -c Release
```

## 2. 目录职责

改动前先确认文件归属，错位会被 `tools/audit-conventions.sh` 拦下。

| 目录 | 只放 | 不放 |
|---|---|---|
| `src/` | 产品代码与工程文件 | 脚本、示例配置、文档 |
| `tests/` | 测试代码与测试资产 | 原生工程源码、构建脚本 |
| `docs/` | 文档 | 可执行脚本、产物 |
| `build/` | 构建、打包、签名、SBOM | 开发辅助客户端、日常运维脚本 |
| `tools/` | 审计、冒烟、迁移、清理等开发/运维工具 | 构建流水线必需脚本 |
| `native/` | Swift 等原生工程 | .NET 代码 |
| `samples/` | 示例配置（如 `models.example.json`） | 真实密钥、生产配置 |
| `artifacts/` | 全部产物（不入库） | 源码、配置、文档 |

## 3. 分支与提交

- 分支：`feat/<scope>`、`fix/<scope>`、`refactor/<scope>`、`docs/<scope>`、`chore/<scope>`。
- 提交信息：`<type>(<scope>): <中文摘要>`，`type` 取 `feat` / `fix` / `refactor` / `docs` / `test` / `chore` / `build`。
- 一个提交只做一件事；跨阶段的改动拆成多个提交，保证任一阶段可单独回滚。
- 版本控制内文件移动一律 `git mv`，保留历史；不 `git add -f` 绕过忽略规则；不提交 >5MB 的二进制。

## 4. 代码约定

- 目标框架 `net10.0`、`Nullable` 开启、警告视为错误（`Directory.Build.props` 已集中设定）。
- 包版本只在 `Directory.Packages.props` 维护（中央包管理），子工程不得写 `Version=`。
- 依赖只允许 MIT / Apache-2.0 / BSD-2-Clause / BSD-3-Clause / MS-PL；引入新依赖需同步 [合规文档](docs/release/compliance.md)。
- 环境变量只通过 `AiAssistant.Core.Configuration.EnvironmentNames` 常量引用，不在代码里写字面量。
- 平台名称在文档与注释中统一写作 `macOS`；`OperatingSystem.IsMacOS()` 与 Apple bundle 的 `Contents/MacOS` 属于不可改标识。
- 产物路径只通过 `ArtifactPaths` 解析，不在代码里拼接 `"artifacts"`。

## 5. 脚本约定

- 位置：`build/<domain>/<script>` 或 `tools/<script>`，每个 domain 有 `README.md`。
- 头部必须 `set -euo pipefail`，仓库根用 `git rev-parse --show-toplevel` 解析，禁止 `../..` 层级推断。
- 必须提供 `--help`；破坏性操作必须提供 `--dry-run` 且默认 dry-run。
- `.sh` 权限位 755；`.ps1` 保存为 UTF-8 无 BOM。
- 退出码约定：`0` 成功、`1` 一般失败、`2` 用法错误。

## 6. 文档约定

- 入口唯一：`README.md`（仓库）与 `docs/README.md`（全部文档索引）。新增文档必须挂到索引，从索引 ≤2 跳可达。
- 每篇文档带四段头信息：状态、最后更新、适用版本、维护触发条件。
- 单一事实源：同一事实（端口、默认模型、路径、门禁状态）只在一处定义，其余用相对链接引用。
- 信息归属：

  | 信息 | 权威文档 |
  |---|---|
  | 架构与依赖 | `docs/architecture/overview.md` + `docs/adr/` |
  | 目录、产物与命名 | `docs/development/conventions-and-artifacts.md` |
  | 测试、Trait、黄金集 | `docs/development/testing-and-benchmarks.md` |
  | CLI / 环境变量 / 配置 | `docs/reference/*.md` |
  | 安全、合规、发布与路线 | `docs/release/*.md` |

- 中文正文、英文标识符与命令；平台名称写 `macOS`；相对链接一律相对当前文件。

## 7. 提交前自检

```bash
dotnet build AiAssistant.sln -c Release --warnaserror
dotnet format AiAssistant.sln --verify-no-changes
dotnet test --solution AiAssistant.sln -c Release
bash tools/audit-conventions.sh
python3 -B build/sbom/generate-sbom.py --verify --check-manifest
git status --ignored --porcelain   # 源码树零逃逸产物
```

测试工程拓扑、Trait 过滤与受限环境 Skip 规则见 [测试与评测](docs/development/testing-and-benchmarks.md)。

新增或修改打包相关脚本时，额外跑：

```bash
bash build/package/legal-files.sh /tmp/legal-check   # 必须产出四个法务文件
```
