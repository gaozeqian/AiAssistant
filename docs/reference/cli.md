# 命令行参数与退出码

> **状态**：living ｜ **最后更新**：2026-11-23 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：增删参数、默认值或退出码语义变化时

本文件是发布态可执行文件 `AiAssistant.exe` / `AiAssistant` / `aiassistant` 的 CLI 权威清单。命令实现位于 `src/AiAssistant.Host/Commands/`。

## 1. 诊断

| 参数 | 说明 |
|---|---|
| `--help` / `-h` | 显示帮助 |
| `--version` | 打印版本 |
| `--smoke-check` | 验证二进制可启动 |
| `--startup-check` | 启动完整运行时后退出，报告恢复任务数 |
| `--capture-smoke [秒]` | 无 UI 运行采集管线 N 秒（5–300，默认 20），报告落库事件/媒体 |
| `--doctor` | 存储健康 + 模型探测（10s）+ MCP + PDF 字体；macOS 额外输出 Helper/TCC |

## 2. 报告与问答

| 参数 | 说明 |
|---|---|
| `--report-range [--from yyyy-MM-dd] [--to yyyy-MM-dd] [--output <目录>]` | 生成 Markdown/SVG/PNG/PDF/stats；缺省近 7 天 |
| `--ask "<问题>" [--days 7] [--output <目录>]` | 基于近 N 天本地证据问答；可选导出 |

默认输出目录为 `artifacts/test-results/reports/`（可用 `--output` 覆盖）。

## 3. 维护

| 参数 | 说明 |
|---|---|
| `--backup <路径>` | 写加密备份 |
| `--restore <路径> --confirm-restore` | 恢复加密备份 |
| `--purge-data --confirm-purge` | 删除全部本地数据 |
| `--rebuild-vectors` | 重建 embedding 索引 |

维护命令会先初始化加密 Schema，再执行。

## 4. 模型

| 参数 | 说明 |
|---|---|
| `--benchmark-model [--output <路径>]` | 固定三用例对话基准；默认 `artifacts/test-results/benchmarks/` |
| `--probe-model` | 打印各档位 chat/stream/embed/json/tools 能力 |
| `--import-model-secret <名称>` | 从标准输入读取密钥并加密存储 |
| `--list-model-secrets` | 列出密钥名（不显示值） |
| `--delete-model-secret <名称> --confirm-delete-secret` | 删除密钥 |

## 5. 本地 MCP

| 参数 | 说明 |
|---|---|
| `--mcp-serve` | 无头运行只读 MCP（需 `AIASSISTANT_MCP_ENABLED=true`） |
| `--mcp-token` | 打印本地 Bearer 令牌 |

## 6. 仅开发宿主

以下命令不在产品二进制中实现（调用会返回退出码 3 并提示正确入口）：

```bash
dotnet run --project tools/AiAssistant.Diagnostics.Host -c Release -- --benchmark-retrieval
dotnet run --project tools/AiAssistant.Diagnostics.Host -c Release -- --benchmark-asr --samples <dir>
dotnet run --project tools/AiAssistant.Diagnostics.Host -c Release -- --benchmark-kws --samples <dir>
dotnet run --project tools/AiAssistant.Diagnostics.Host -c Release -- --benchmark-ocr --samples <dir>
dotnet run --project tools/AiAssistant.Diagnostics.Host -c Release -- --ui-e2e
```

## 7. 退出码

| 码 | 语义 |
|---|---|
| `0` | 成功 |
| `1` | 运行完成但健康/证据/基准失败（数据库不健康、无证据、基准成功率为 0、采集 0 事件、密钥不存在） |
| `2` | 启动失败（异常、不可用数据目录、平台初始化失败） |
| `3` | 用法错误或缺少破坏性操作确认（参数缺值、日期非法、`--confirm-*` 缺失、命令已迁移到诊断宿主） |

## 8. 约定

- 日期格式固定 `yyyy-MM-dd`；整数参数越界会被钳制到允许区间。
- 破坏性命令必须带对应 `--confirm-*`；缺失时返回 3，不执行任何写入。
- 控制台统一 UTF-8；无控制台环境（服务/托盘）自动降级。
- 全局配置见 [配置](configuration.md)，环境变量见 [环境变量](environment-variables.md)。
