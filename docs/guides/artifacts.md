# 产物目录与保留策略

> **状态**：living ｜ **最后更新**：2026-11-22 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：`artifacts/` 分层、保留策略或清理脚本发生变化时

`artifacts/` 存放全部产物，整体被 `.gitignore` 忽略（`git ls-files artifacts` 恒为空）。

单一事实源：本文件定义分层与保留策略；代码侧由 `AiAssistant.Core.Configuration.ArtifactPaths` 解析，脚本侧读 `AIASSISTANT_ARTIFACTS_ROOT`。目录结构变更必须同时改这两处与本文件。

## 1. 分层

| 子目录 | 内容 | 生命周期 | 保留策略 | 清理 |
|---|---|---|---|---|
| `build/` | `dotnet publish` 输出、打包 staging、`sbom/` | 构建输出 | 每次构建覆盖 | 随时可删 |
| `package/` | `zip` / `msi` / `pkg` / `deb` / `cer` / `SHA256SUMS` | 可分发包 | 最近 1 个版本 | `clean-artifacts.sh --apply` |
| `cache/models/` | 语音与 OCR 模型权重、`downloads/` 压缩包缓存、`bundle/` 随包载荷 | 外部资产缓存 | 本地缓存，可重建 | 仅 `--models` |
| `evidence/e2e/` | UI E2E 截图与 `report.md` | 测试证据 | 最近 5 轮 | 轮转 |
| `evidence/benchmarks/` | ASR / OCR / KWS / 检索基准报告 | 测试证据 | 全量保留 | 不清理 |
| `evidence/soak/` | 浸泡报告 | 测试证据 | 全量保留 | 不清理 |
| `evidence/reports/` | CLI `--report-range` 导出 | 运行时导出 | 全量保留 | 不清理 |
| `evidence/golden/` | 基准输入样本（`--samples` 默认目录） | 测试输入 | 全量保留 | 不清理 |
| `evidence/packages/` | 一次性包布局验证（`linux-check` / `macos-check`） | 一次性证据 | 归档 | 仅 `--packages` |
| `evidence/retired-models/` | 退役模型证据（`tts-aishell3`） | 永久证据 | 永久保留 | **永不清理** |
| `tmp/` | 测试临时目录 | 临时文件 | 测试结束自清理 | 随时可删 |

## 2. 路径解析

| 使用方 | 解析方式 | 默认值 |
|---|---|---|
| .NET 代码 | `ArtifactPaths.Root` / `ArtifactPaths.Resolve(...)` | `<当前工作目录>/artifacts` |
| 测试 | `ArtifactPaths.Resolve(repoRoot, "tmp", ...)` | `<仓库根>/artifacts` |
| shell / PowerShell 脚本 | `ARTIFACTS_ROOT="${AIASSISTANT_ARTIFACTS_ROOT:-$REPO_ROOT/artifacts}"` | `<仓库根>/artifacts` |
| Python 脚本 | `ARTIFACTS_ROOT = Path(os.environ.get("AIASSISTANT_ARTIFACTS_ROOT", "artifacts"))` | `<当前目录>/artifacts` |

`AIASSISTANT_ARTIFACTS_ROOT` 是**唯一**的整体搬迁开关；不允许在代码或脚本里再出现 `"artifacts"` 字面量。

## 3. 测试写入规则

- 测试只写系统临时目录或 `artifacts/tmp/`，结束自清理。
- 报告类输出（E2E、基准、浸泡）写 `artifacts/evidence/` 对应子目录；这类文件是发布证据，不随 `--apply` 删除。
- 不得把 `artifacts/` 下的绝对路径写进断言或测试数据。

## 4. 清理入口

```bash
bash tools/clean-artifacts.sh                 # dry-run，打印计划与占用体积
bash tools/clean-artifacts.sh --apply         # 逐目录确认后删除
bash tools/clean-artifacts.sh --apply --yes   # 非交互（CI/脚本）
bash tools/clean-artifacts.sh --models --apply  # 连带清理模型缓存（跳过 _retired）
```

默认 dry-run；`--apply` 后每个子目录单独确认；非交互环境必须显式 `--yes`。

## 5. 重建

清空可再生产物后，按下列命令重建（模型缓存清空时需先下载）：

```bash
dotnet restore AiAssistant.sln
dotnet build AiAssistant.sln -c Release

# 模型载荷（缓存为空时先 fetch，约 1.7GB 下载）
bash build/models/fetch-bundle.sh          # 或已有缓存时仅 stage
bash build/models/stage-bundle.sh --dry-run
bash build/models/stage-bundle.sh

# 安装包（按平台选择）
pwsh build/package/package-windows.ps1 -Version 0.1.0
bash build/package/package-macos.sh --skip-sign
bash build/package/package-linux.sh

# SBOM
python build/sbom/generate-sbom.py --verify
```

## 6. 退役模型

`evidence/retired-models/tts-aishell3/` 保存 AISHELL-3 权重与压缩包，仅作许可审计证据：

- 不参与打包：`build/models/stage-bundle.sh` 会拒绝任何路径含 `aishell3` 的 bundle。
- 不被应用解析：`AIASSISTANT_SHERPA_MODEL_DIR` 与数据目录模型路径都不会指向该目录。
- 不参与清理：`clean-artifacts.sh` 显式跳过。

是否退役的判定与依据见 [模型资产与许可](../compliance/model-assets.md)。
