# 配置

> **状态**：living ｜ **最后更新**：2026-11-23 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：数据目录、配置键、默认模型、保留策略或端点策略变化时

本文件是数据目录、`models.json` 与默认值的唯一权威；环境变量见 [environment-variables.md](environment-variables.md)，命令行见 [cli.md](cli.md)。

## 1. 数据目录

| 平台 | 默认路径 |
|---|---|
| Windows | `%LOCALAPPDATA%\AiAssistant` |
| macOS | `~/Library/Application Support/AiAssistant` |
| Linux | `~/.local/share/AiAssistant` |

覆盖：`AIASSISTANT_DATA_DIRECTORY`。目录内存放加密数据库、系统密钥库引用、媒体库、向量索引、语音日志与 `models.json`。

## 2. 加密与保留

| 项 | 默认 |
|---|---|
| 数据库 | SQLite3MC 加密；master key 由系统密钥库保护 |
| 媒体 | AES-GCM；默认保留 30 天，每小时扫描一次过期媒体 |
| 命令音频 | 默认不落盘；开启后默认保留 7 天（1–365） |
| 日报 | 每日 18:30 生成；版本追加，不覆盖历史 |

数据删除、备份与恢复见 [运维指南](../guides/operations.md)。

## 3. `models.json`

默认路径 `<数据目录>/models.json`，覆盖 `AIASSISTANT_MODELS_CONFIG`。示例见 `samples/models.example.json`。

| 配置键 | 含义 |
|---|---|
| `schemaVersion` | 配置 schema 版本（当前 `1`） |
| `policy.defaultTier` | 默认档位：`LocalOffline` / `CorporateIntranet` / `PublicCloud` |
| `policy.allowIntranet` | 是否允许企业内网端点 |
| `policy.allowPublicCloud` | 是否允许公网端点 |
| `policy.requirePublicCloudConsent` | 公网是否必须运行时显式同意 |
| `policy.allowLocalFallback` | 内网/公网失败时是否允许本机兜底 |
| `endpoints[].tier` | 端点档位 |
| `endpoints[].provider` | 提供方（本机默认 `ollama`） |
| `endpoints[].baseUrl` | 端点地址；loopback 端点绕过系统代理 |
| `endpoints[].chatModel` | 对话模型 |
| `endpoints[].embeddingModel` | 向量模型 |
| `endpoints[].think` / `thinkingLevel` | 思考开关与强度（`low`/`medium`/`high`） |
| `endpoints[].apiKeySecret` | 加密密钥库中的名称；禁止明文 |
| `embedding` | 可选的独立向量端点文档（同 tier 时复用对话端点） |

企业端点还支持 `enterpriseCaPath`、客户端证书路径、`proxyUrl` 与 `timeoutSeconds`。公网档即使配置，也必须 `allowPublicCloud=true` 且运行时显式同意。

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
