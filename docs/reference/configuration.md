# 配置

> **状态**：living ｜ **最后更新**：2026-11-22 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：配置键、默认模型、数据目录或端点策略变化时

本文件是配置键、默认模型与数据目录语义的**唯一权威**。环境变量见 [environment-variables.md](environment-variables.md)，命令行见 [cli.md](cli.md)。

## 1. 数据目录

| 平台 | 默认路径 |
|---|---|
| Windows | `%LOCALAPPDATA%\AiAssistant` |
| macOS | `~/Library/Application Support/AiAssistant` |
| Linux | `~/.local/share/AiAssistant` |

覆盖：`AIASSISTANT_DATA_DIRECTORY`。数据目录内存放加密数据库、密钥库、媒体库、向量索引、语音日志与 `models.json`。

## 2. 模型端点（`models.json`）

示例见仓库 `samples/models.example.json`。默认路径 `<数据目录>/models.json`，覆盖 `AIASSISTANT_MODELS_CONFIG`。

| 配置键 | 含义 |
|---|---|
| `schemaVersion` | 配置 schema 版本（当前 `1`） |
| `policy.defaultTier` | 默认档位：`LocalOffline` / `CorporateIntranet` / `PublicCloud` |
| `policy.allowIntranet` | 是否允许内网端点 |
| `policy.allowPublicCloud` | 是否允许公网端点 |
| `endpoints[].tier` | 端点所属档位 |
| `endpoints[].baseUrl` | 端点地址（默认本机 `http://127.0.0.1:11434`） |
| `endpoints[].chatModel` | 对话模型（默认 `gemma4:12b`） |
| `endpoints[].embeddingModel` | 向量模型（默认 `embeddinggemma-2:740m`） |
| `endpoints[].apiKey` | 密钥；用 `env:VAR` 引用环境变量或走加密密钥库，禁明文 |

端点密钥通过 AES-GCM 加密密钥库托管（`--import-model-secret`）；公网端点需用户显式同意。

## 3. 默认模型

| 用途 | 默认 |
|---|---|
| 对话 | `gemma4:12b` |
| Embedding | `embeddinggemma-2:740m`（768 维，官方查询前缀，title/text 文档格式） |

默认值定义在 `AiAssistant.Models.ModelGatewayOptions`；`docs/MODELS.md` 与 `samples/models.example.json` 不再各自定义，只引用本表。

## 4. 语音模型

随包资产：KWS（zipformer wenetspeech）、Paraformer ASR、MeloTTS TTS、Silero VAD。安装与来源见 [guides/models.md](../guides/models.md)，许可见 [compliance/model-assets.md](../compliance/model-assets.md)。

## 5. 本地 MCP

默认关闭；启用后只监听 `127.0.0.1:47831`，要求 Bearer 令牌，四个只读工具。端口/限流/开关见 [environment-variables.md](environment-variables.md)。
