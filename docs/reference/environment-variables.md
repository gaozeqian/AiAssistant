# 环境变量

> **状态**：living ｜ **最后更新**：2026-11-22 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：新增/删除环境变量或改默认值时

本文件是全部环境变量的**唯一权威**。代码只允许通过 `AiAssistant.Core.Configuration.EnvironmentNames` 常量引用，禁止散落字面量。默认模型、数据目录与配置键见 [configuration.md](configuration.md)。

## 数据与路径

| 变量 | 作用 | 默认 |
|---|---|---|
| `AIASSISTANT_DATA_DIRECTORY` | 应用数据目录 | 各平台 `LocalApplicationData` 下的 `AiAssistant` |
| `AIASSISTANT_ARTIFACTS_ROOT` | 整体搬迁产物目录 | `<当前目录>/artifacts` |
| `AIASSISTANT_CAPTURE_DIRECTORIES` | 采集目录白名单（`;` 分隔） | 空 |
| `AIASSISTANT_MODELS_CONFIG` | `models.json` 路径 | `<数据目录>/models.json` |
| `AIASSISTANT_SHERPA_MODEL_DIR` | sherpa 模型目录 | 数据目录 `models/sherpa` → 应用目录 `models/sherpa` |
| `AIASSISTANT_WHISPER_MODEL_PATH` | Whisper ggml 模型路径 | 数据目录 `models/whisper/ggml-small.bin` |
| `AIASSISTANT_RAPIDOCR_MODEL_DIR` | RapidOCR 模型目录 | 应用目录内 models |
| `AIASSISTANT_MAC_HELPER_PATH` | Swift Helper 可执行文件 | 应用目录 `Contents/MacOS/aiassistant-mac-helper` |
| `AIASSISTANT_PDF_FONT_PATH` | PDF 导出的中文字体 | 内置回退 |
| `AIASSISTANT_IPC_TOKEN` | 本地 IPC 会话令牌 | 运行时随机 |

## 模型网关

| 变量 | 作用 | 默认 |
|---|---|---|
| `AIASSISTANT_MODEL_PROVIDER` | 端点提供方 | 空（按配置） |
| `AIASSISTANT_MODEL_BASE_URL` | 本机/内网端点地址 | `http://127.0.0.1:11434` |
| `AIASSISTANT_CHAT_MODEL` | 对话模型 | `gemma4:12b` |
| `AIASSISTANT_EMBEDDING_MODEL` | 向量模型 | `embeddinggemma-2:740m` |
| `AIASSISTANT_EMBEDDING_QUERY_PREFIX` | 检索查询前缀 | 按模型内置 |
| `AIASSISTANT_EMBEDDING_DOCUMENT_STYLE` | 文档向量格式（title/text） | 按模型内置 |
| `AIASSISTANT_MODEL_THINK` | 思考开关 | 关 |
| `AIASSISTANT_MODEL_THINKING_LEVEL` | 思考档位 | 按配置 |
| `AIASSISTANT_MODEL_API_KEY` | 端点 API Key（也可走加密密钥库） | 空 |
| `AIASSISTANT_MODEL_ALLOW_REMOTE` | 允许公网端点 | 关 |

## 语音

| 变量 | 作用 | 默认 |
|---|---|---|
| `AIASSISTANT_ASR_ENGINE` | `sherpa` / `whisper` | `sherpa` |
| `AIASSISTANT_KWS_SCORE` | 唤醒得分阈值（0.1–10） | 内置常量 |
| `AIASSISTANT_KWS_THRESHOLD` | 唤醒判定阈值（0.01–0.99） | 内置常量 |
| `AIASSISTANT_VOICE_DISABLE_CHIME` | `true` 关闭提示音 | 开（有提示音） |
| `AIASSISTANT_VOICE_RECORD_COMMAND_AUDIO` | `true` 记录命令音频 | 关 |
| `AIASSISTANT_VOICE_AUDIO_RETENTION_DAYS` | 命令音频保留天数 | 7 |

## 报告

| 变量 | 作用 | 默认 |
|---|---|---|
| `AIASSISTANT_REPORT_TIME` | 每日日报时间 `HH:mm` | `18:30` |
| `AIASSISTANT_REPORT_DISABLE_SCHEDULE` | `true` 关闭定时日报 | 开（定时） |

## 本地 MCP

| 变量 | 作用 | 默认 |
|---|---|---|
| `AIASSISTANT_MCP_ENABLED` | `true` 启用只读 MCP | 关 |
| `AIASSISTANT_MCP_PORT` | MCP 端口 | `47831` |
| `AIASSISTANT_MCP_RPM` | 每分钟请求上限 | `60` |

## UI

| 变量 | 作用 | 默认 |
|---|---|---|
| `AIASSISTANT_THEME` | `light` / `dark` 固定主题 | 跟随系统 |
| `AIASSISTANT_UI_E2E` | 启用自驱动 UI E2E | 关 |
| `AIASSISTANT_UI_E2E_SHOW` | E2E 时显示窗口 | 关 |
| `AIASSISTANT_UI_E2E_OUTPUT` | E2E 截图与报告目录 | `artifacts/evidence/e2e` |

## 测试与诊断

| 变量 | 作用 | 默认 |
|---|---|---|
| `AIASSISTANT_SOAK_DAYS` | 多日加速浸泡天数 | `3` |
| `AIASSISTANT_REAL_SOAK_MINUTES` | 真实时钟浸泡分钟数（10–120） | 关（CI 保持快） |
| `AIASSISTANT_MAC_HELPER_E2E` | 真机 Helper 测试开关 | 关 |
| `AIASSISTANT_PORTAL_E2E` | Linux portal 真机测试开关 | 关 |
| `AIASSISTANT_LINUX_BACKEND` | 强制 Linux 后端 | 自动 |
| `AIASSISTANT_WAYLAND_PIPEWIRE` | `false` 关闭 PipeWire | 开 |
| `AIASSISTANT_TEST_SECRET_*` | 测试用密钥前缀（仅供测试） | 无 |
