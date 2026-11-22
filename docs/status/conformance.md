# 规范合规矩阵

> **状态**：living ｜ **最后更新**：2026-11-22 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：`tools/audit-conventions.sh` 结果变化时

本文件是 R1–R10 规范的合规矩阵，内容由 `tools/audit-conventions.sh` 的可复跑结果支撑，两者必须一致。
豁免项见 [../guides/conventions.md](../guides/conventions.md) 豁免表。

| 规则 | 状态 | 证据 | 豁免 |
|---|---|---|---|
| R1 根目录白名单 | 待审计 | `tools/audit-conventions.sh` | — |
| R2 目录职责单一 | 待审计 | 各二级目录 README | — |
| R3 命名 | 待审计 | 审计脚本拼写/命名扫描 | `OperatingSystem.IsMacOS()` 等 |
| R4 文档体系 | 待审计 | 链接检查 + 索引可达性 | — |
| R5 产物治理 | 待审计 | `git ls-files artifacts` 为 0 + 集中解析 | `"artifacts"` 3 处解析点 |
| R6 构建与工具 | 待审计 | 脚本骨架 + 权限位 | — |
| R7 CI | 待审计 | workflow 路径核对 | 未实跑（账单） |
| R8 元文件与许可 | 待审计 | 法务文件 + 依赖/模型白名单 | `CODE_OF_CONDUCT.md` |
| R9 配置与常量 | 待审计 | 环境变量常量收敛 | — |
| R10 豁免与可追溯 | 待审计 | 豁免表 + 审计脚本 | — |
