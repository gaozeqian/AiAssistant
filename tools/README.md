# tools/ — 开发与运维工具

本目录放「人手动跑」的工具：审计、清理、冒烟、迁移、联调。构建流水线必需的脚本放 `build/`。

| 路径 | 用途 |
|---|---|
| `audit-conventions.sh` | 仓库规范审计（R1–R9 逐条），CI 与本地共用 |
| `clean-artifacts.sh` | 按保留策略清理 `artifacts/`，默认 dry-run |
| `install-voice-models.sh` | 把已下载的语音模型装到应用数据目录 |
| `install-desktop-entry.sh` | Linux 桌面入口与图标 |
| `mcp/` | 只读本地 MCP 的联调客户端（Python 一致性客户端 + Inspector 冒烟） |

## 约定

- 与 `build/` 相同的脚本骨架：`set -euo pipefail`、`git rev-parse --show-toplevel` 解析仓库根、`--help`、破坏性操作 `--dry-run`。
- 工具不得被 `build/package/*` 或 CI 打包作业依赖；否则应移入 `build/`。
- 新增工具需在本表登记一行。

## 常用

```bash
bash tools/audit-conventions.sh          # 规范审计
bash tools/clean-artifacts.sh --help     # 查看保留策略
bash tools/clean-artifacts.sh --apply    # 真正删除（按子目录确认）
```
