# 命令行参数

> **状态**：living ｜ **最后更新**：2026-11-22 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：增删命令行参数或改默认行为时

本文件是 `AiAssistant.Host` 命令行参数的**唯一权威**。默认输出目录的语义见 [artifacts.md](../guides/artifacts.md)，配置与环境变量见 [configuration.md](configuration.md) 与 [environment-variables.md](environment-variables.md)。

## 诊断

| 参数 | 说明 |
|---|---|
| `--help` / `-h` | 显示帮助 |
| `--version` | 打印版本 |
| `--smoke-check` | 验证二进制可启动 |
| `--startup-check` | 初始化存储后退出 |
| `--capture-smoke [秒]` | 无 UI 跑采集流水线并报告落库事件 |
| `--doctor` | 存储健康 + 模型探测 + MCP 状态 |

## 报告与问答

| 参数 | 说明 |
|---|---|
| `--report-range --from <日期> --to <日期> [--output <目录>]` | 生成 Markdown + SVG + stats，不启动 UI |
| `--ask "<问题>" [--days 7] [--output <目录>]` | 基于本地证据的问答 |

## 维护

| 参数 | 说明 |
|---|---|
| `--backup <路径>` | 写加密备份 |
| `--restore <路径> --confirm-restore` | 恢复加密备份（需显式确认） |
| `--purge-data --confirm-purge` | 删除全部采集数据（需显式确认） |
| `--rebuild-vectors` | 重建 embedding 索引 |

## 模型

| 参数 | 说明 |
|---|---|
| `--benchmark-model [--output <p>]` | 固定对话基准 |
| `--benchmark-retrieval [--output <p>]` | 检索质量基准 |
| `--benchmark-asr [--samples <目录>] [--output <p>]` | 中文 ASR 黄金集，报告 CER |
| `--benchmark-ocr [--samples <目录>] [--output <p>]` | OCR 黄金集（Linux RapidOCR） |
| `--benchmark-kws [--samples <目录>] [--output <p>]` | 唤醒词黄金集 |
| `--probe-model` | 打印端点能力 |
| `--import-model-secret <名称>` | 从 stdin 读取密钥并加密存储 |
| `--list-model-secrets` | 列出密钥名（永不显示值） |
| `--delete-model-secret <名称> --confirm-delete-secret` | 删除密钥（需显式确认） |

## 本地 MCP

| 参数 | 说明 |
|---|---|
| `--mcp-serve` | 无头运行只读 MCP 端点（默认关闭，见 `AIASSISTANT_MCP_ENABLED`） |
| `--mcp-token` | 打印本地 bearer 令牌 |

## 约定

- 日期格式 `yyyy-MM-dd`；`--from`/`--to` 缺省为近 7 天。
- 破坏性维护命令（restore / purge / delete-secret）必须带对应的 `--confirm-*` 才会执行。
- `--output` 缺省落到 `artifacts/evidence/` 对应子目录；`--samples` 缺省落到 `artifacts/evidence/golden/`。
