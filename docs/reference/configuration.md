# 配置

> **状态**：living ｜ **最后更新**：2026-11-26 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：数据目录、配置键、默认模型、保留策略或端点策略变化时

本文件是数据目录、`models.json` 与默认值的唯一权威；环境变量见 [environment-variables.md](environment-variables.md)，命令行见 [cli.md](cli.md)。

## 1. 数据目录

| 平台 | 默认路径 |
|---|---|
| Windows | `%LOCALAPPDATA%\AiAssistant` |
| macOS | `~/Library/Application Support/AiAssistant` |
| Linux | `~/.local/share/AiAssistant` |

覆盖：`AIASSISTANT_DATA_DIRECTORY`。目录内存放加密数据库、系统密钥库引用、媒体库、向量索引、语音日志、`models.json` 与 `report-schedule.json`（日报定时设置）。

## 2. 加密与保留

| 项 | 默认 |
|---|---|
| 数据库 | SQLite3MC 加密；master key 由系统密钥库保护 |
| 媒体 | AES-GCM；默认保留 30 天，每小时扫描一次过期媒体 |
| 命令音频 | 默认不落盘；开启后默认保留 7 天（1–365） |
| 日报 | 每日 00:30（可在「日报与站会」页修改）或应用启动时检查前一天：无日报且有活动记录时生成；已有日报但生成后又有活动记录时自动追加新版本 |

数据删除、备份与恢复见 [运维指南](../guides/operations.md)。

### 日报定时设置

桌面端「日报与站会」页的「自动日报」卡片保存到 `<数据目录>/report-schedule.json`：

```json
{
  "time": "00:30",
  "enabled": true
}
```

`time` 为本地时间 `HH:mm`，检查目标固定为前一天。删除该文件后会回退到 `AIASSISTANT_REPORT_TIME` / `AIASSISTANT_REPORT_DISABLE_SCHEDULE` 的初始值。

## 3. `models.json`

默认路径 `<数据目录>/models.json`，覆盖 `AIASSISTANT_MODELS_CONFIG`。示例见 `samples/models.example.json`。

Schema 3 只保存当前在用的两个模型，二者完全独立（可同档位、不同地址、不同密钥）。所有端点统一走 OpenAI 兼容协议，不再有 provider 字段：

```json
{
  "schemaVersion": 3,
  "chat":      { "tier": "PublicCloud", "baseUrl": "https://api.deepseek.com", "model": "deepseek-chat", "apiKeySecret": "chat-endpoint-key", "allowRemoteEndpoint": true },
  "embedding": { "tier": "PublicCloud", "baseUrl": "https://api.siliconflow.cn", "model": "BAAI/bge-m3", "apiKeySecret": "embedding-endpoint-key", "allowRemoteEndpoint": true }
}
```

| 配置键 | 含义 |
|---|---|
| `schemaVersion` | 配置 schema 版本（当前 `3`；`1`/`2` 仍可读取并在保存时升级为 `3`） |
| `chat` | 当前对话模型端点（必填） |
| `embedding` | 当前向量模型端点（可选；缺省时继承 `chat` 的地址与密钥，模型取默认 embedding） |
| `*.tier` | 端点档位：`LocalOffline` / `CorporateIntranet` / `PublicCloud` |
| `*.baseUrl` | 端点地址（http/https，不含 `/v1` 路径）；loopback 端点绕过系统代理 |
| `*.model` | 模型名（对话或向量） |
| `*.apiKeySecret` | 加密密钥库中的名称；禁止明文 |
| `*.think` / `thinkingLevel` | 思考开关与强度（仅对话端点，`low`/`medium`/`high`），映射为 `reasoning_effort` |
| `*.timeoutSeconds` / `proxyUrl` / `enterpriseCaPath` / 客户端证书 | 企业端点可选项 |

规则：

- 端点统一调用 `/v1/chat/completions`、`/v1/embeddings`、`/v1/models`；Ollama、LM Studio、vLLM 与云厂商只差地址、模型名和密钥。
- 本地模型不是必须项；不再要求 `LocalOffline` 端点或 `defaultTier`。
- 档位即策略：选择 `PublicCloud` 视为显式同意公网，选择 `CorporateIntranet` 即允许内网；`defaultTier` 始终跟随 `chat.tier`。
- `LocalOffline` 端点必须是 loopback；内网/公网端点必须 `allowRemoteEndpoint=true`（UI 保存时自动写入）。
- 未使用的档位与端点不会写入文件。

## 4. 默认模型

| 用途 | 默认 |
|---|---|
| 对话 | `gemma4:12b` |
| Embedding | `embeddinggemma-2:740m`（768 维） |

默认值定义在 `AiAssistant.Core.Configuration.ModelDefaults`；`AIASSISTANT_CHAT_MODEL` 等环境变量可覆盖。

## 5. 语音模型解析

顺序：`AIASSISTANT_SHERPA_MODEL_DIR` → `<数据目录>/models/sherpa` → `<应用目录>/models/sherpa`。数据目录优先，允许用户覆盖随包资产。安装与目录结构见 [模型指南](../guides/models.md)。

## 6. 本地 MCP

默认关闭；启用后只监听 `127.0.0.1:47831`，Bearer 令牌，四个只读工具。端口、限流与开关见 [environment-variables.md](environment-variables.md)。

## 7. 产物目录

`AIASSISTANT_ARTIFACTS_ROOT` 覆盖默认 `<仓库根>/artifacts`；仅影响测试、基准、打包与 SBOM，不影响用户数据目录。
