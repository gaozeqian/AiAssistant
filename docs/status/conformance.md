# 规范合规矩阵

> **状态**：living ｜ **最后更新**：2026-11-23 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：`tools/audit-conventions.sh` 结果变化时

本文件是 R1–R10 规范的合规矩阵，由 `tools/audit-conventions.sh` 的可复跑结果支撑，两者保持一致（2026-11-23：43 项检查，0 不合规）。豁免项见 [../guides/conventions.md](../guides/conventions.md) 豁免表。

| 规则 | 状态 | 证据 | 豁免 |
|---|---|---|---|
| R1 根目录白名单 | ✅ 合规 | 根目录逐项比对；`.workbuddy/`、`.workbuddy-ai/` 均以带斜杠规则忽略 | `samples/` 增补进白名单（规范修订） |
| R2 目录职责单一 | ✅ 合规 | 7 个二级目录均有 README；E2E/基准宿主迁至 `tools/AiAssistant.Diagnostics.Host/`；7 个上帝类已拆分为组件 | `src/` 保持扁平 16 工程 |
| R3 命名 | ✅ 合规 | 平台拼写唯一 `macOS`；文档/目录小写连字符；`.sh` 全 755；阶段代号测试类已按领域语义改名 | BCL API、`Contents/MacOS`、Swift 包名 |
| R4 文档体系 | ✅ 合规 | `docs/README.md` 唯一索引；相对链接 0 断链；全仓文档 ≤2 跳可达；四段头信息齐全；UTF-8 无 BOM | 历史日志保留旧路径叙述 |
| R5 产物治理 | ✅ 合规 | `git ls-files artifacts` = 0；统一 `artifacts/` 布局；重复 PNG 已删除并改 MSBuild Link | — |
| R6 构建与工具 | ✅ 合规 | `dotnet-tools.json` 在 `.config/`；`.sh/.ps1/.py` 均提供帮助与退出码；构建脚本无 GNU 专属命令 | — |
| R7 CI | ✅ 合规 | 最小 `permissions`、`concurrency`、`timeout-minutes`、Action SHA 锁定；format/ShellCheck/审计/SBOM 作业齐备 | CI 未在本机实跑（GitHub 账单，见 [status.md](status.md)） |
| R8 元文件与许可 | ✅ 合规 | CycloneDX 1.6 SBOM 覆盖 120 个 NuGet 组件 + Swift helper + 4 个本地模型；许可证白名单 100% | `CODE_OF_CONDUCT.md`（非开源场景） |
| R9 配置与常量 | ✅ 合规 | 环境变量字面量仅 `EnvironmentNames.cs`；模型地址/名称收敛到 `ModelDefaults`；无日志 catch 归零 | — |
| R10 豁免与可追溯 | ✅ 合规 | 豁免表 + 审计脚本可复跑 | — |

## 本轮新增的自动化门禁

| 检查 | 归属 | 结果 |
|---|---|---|
| 生产代码禁直接调用系统时钟（ADR-0009） | 审计脚本 + `DependencyRuleTests.Rule9` | PASS |
| 16 模块精确依赖矩阵 | `DependencyRuleTests.Rule7` | PASS |
| 生产程序集禁止暴露测试/基准类型 | `DependencyRuleTests.Rule8` | PASS |
| MCP `Origin` / `Host` 头白名单、鉴权先于限流 | `LoopbackMcpServer` | 已实现；待补专项回归测试 |
| `X509Certificate2` 生命周期随 Handler 释放 | `ModelHttpClientFactory` | 已实现 |
| UTF-8 BOM / 文档四字段 / 2 跳可达 / 重复二进制 | 审计脚本 | PASS |

## 未实跑项

| 项目 | 原因 | 影响 |
|---|---|---|
| CI 打包/测试作业 | GitHub 账单/支出限额（仓库已记录） | 配置已对齐，未实跑 |
| 安装包重打包 | 需下载 1.7GB 模型与真机签名 | 脚本 `--help`/`--dry-run` 已验证 |
| Linux GNU 侧脚本实跑 | 本机仅 macOS | 已移除全部 GNU 专属命令，最终以 CI ubuntu-latest 验证 |
