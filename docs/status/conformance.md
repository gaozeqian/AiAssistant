# 规范合规矩阵

> **状态**：living ｜ **最后更新**：2026-11-22 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：`tools/audit-conventions.sh` 结果变化时

本文件是 R1–R10 规范的合规矩阵，由 `tools/audit-conventions.sh` 的可复跑结果支撑，两者保持一致（2026-11-22：38 项检查，0 不合规）。豁免项见 [../guides/conventions.md](../guides/conventions.md) 豁免表。

| 规则 | 状态 | 证据 | 豁免 |
|---|---|---|---|
| R1 根目录白名单 | ✅ 合规 | 根目录逐项比对通过；`.workbuddy/` 已忽略 | `samples/` 增补进白名单（规范修订） |
| R2 目录职责单一 | ✅ 合规 | 7 个二级目录均有 README；`build/mcp` 已迁 `tools/` | `src/` 保持扁平 16 工程 |
| R3 命名 | ✅ 合规 | 平台拼写唯一 `macOS`；文档/目录小写连字符；`.sh` 全 755 | `OperatingSystem.IsMacOS()`、`Contents/MacOS`、Swift 包名 |
| R4 文档体系 | ✅ 合规 | `docs/README.md` 唯一索引；相对链接 0 断链；头信息齐全 | 历史日志保留旧路径叙述 |
| R5 产物治理 | ✅ 合规 | `git ls-files artifacts` = 0；代码无 `"artifacts"` 字面量；清理入口就绪 | `.ps1`/`.py` 各 1 处解析点 |
| R6 构建与工具 | ✅ 合规 | `dotnet-tools.json` 在 `.config/`；脚本 `rev-parse` + `--help` + `--dry-run` | — |
| R7 CI | ✅ 合规 | workflow 路径对齐；无 MSIX 构建步骤；新增 NuGet 缓存与 conventions 作业 | CI 未实跑（GitHub 账单，见 [status.md](status.md)） |
| R8 元文件与许可 | ✅ 合规 | 法务三件套 + CHANGELOG/CONTRIBUTING/SECURITY/.gitattributes/GitHub 模板齐备；四打包脚本接入 legal-files；依赖与模型白名单通过 | `CODE_OF_CONDUCT.md`（非开源场景） |
| R9 配置与常量 | ✅ 合规 | 41 个环境变量收敛到 `EnvironmentNames`，字面量仅定义文件 | — |
| R10 豁免与可追溯 | ✅ 合规 | 豁免表 8 项 + 审计脚本可复跑 | — |

## 未实跑项

| 项目 | 原因 | 影响 |
|---|---|---|
| `dotnet test` 全量 | 本机 VSTest 宿主回环 socket 崩溃（`0xC0000096`），沙箱内外均复现，与本次改造无关 | 测试项以历史结论（osx-arm64 217/217）+ 构建 0 警告为准 |
| CI 打包/测试作业 | GitHub 账单/支出限额（仓库已记录） | 配置已对齐，未实跑 |
| 安装包重打包 | 需下载 1.7GB 模型与真机签名 | 脚本 `--help`/`--dry-run` 已验证 |
