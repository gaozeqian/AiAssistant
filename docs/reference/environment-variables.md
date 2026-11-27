# 环境变量

> **状态**：living ｜ **最后更新**：2026-11-27 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：新增/删除环境变量或改默认值时

本文件是全部环境变量的唯一权威。代码只能通过 `AiAssistant.Core.Configuration.EnvironmentNames` 常量引用，禁止字面量散落（审计 R9）。

## 数据与路径

| 变量 | 作用 | 默认 |
|---|---|---|
| `AIASSISTANT_DATA_DIRECTORY` | 应用数据目录 | 各平台 `LocalApplicationData/AiAssistant` |
| `AIASSISTANT_ARTIFACTS_ROOT` | 产物根 | `<仓库根>/artifacts`（向上探测 `AiAssistant.sln`） |
| `AIASSISTANT_CAPTURE_DIRECTORIES` | 采集目录白名单（路径分隔符） | 空 |
| `AIASSISTANT_MODELS_CONFIG` | `models.json` 路径 | `<数据目录>/models.json` |
| `AIASSISTANT_SHERPA_MODEL_DIR` | sherpa 模型根目录 | 数据目录 → 应用目录 `models/sherpa` |
| `AIASSISTANT_WHISPER_MODEL_PATH` | Whisper ggml 模型 | 按模型解析顺序 |
| `AIASSISTANT_RAPIDOCR_MODEL_DIR` | RapidOCR 模型目录（Windows/Linux OCR 兜底） | 随包 → 数据目录 → 应用目录 `models/rapidocr` |
| `AIASSISTANT_MAC_HELPER_PATH` | Swift Helper 可执行文件 | `.app/Contents/MacOS/aiassistant-mac-helper` |
| `AIASSISTANT_PDF_FONT_PATH` | PDF 中文字体 | 内置回退 |
| `AIASSISTANT_IPC_TOKEN` | Helper IPC 一次性令牌 | 运行时随机 |

## 备份

| 变量 | 作用 | 默认 |
|---|---|---|
| `AIASSISTANT_BACKUP_KEY` | CLI 恢复时替代 `--backup-key` 传入备份密钥，避免密钥出现在进程列表或命令历史 | 空 |

> 备份密钥是高熵随机值，只在导出时显示一次；不要写入脚本、提交到仓库或与备份文件放在一起。

## 模型网关

| 变量 | 作用 | 默认 |
|---|---|---|
| `AIASSISTANT_MODEL_BASE_URL` | 本机/内网端点 | `http://127.0.0.1:11434` |
| `AIASSISTANT_CHAT_MODEL` | 对话模型 | `gemma4:12b` |
| `AIASSISTANT_EMBEDDING_MODEL` | 向量模型 | `embeddinggemma-2:740m` |
| `AIASSISTANT_EMBEDDING_QUERY_PREFIX` | 查询指令前缀 | 按模型内置 |
| `AIASSISTANT_EMBEDDING_DOCUMENT_STYLE` | 文档向量格式 | 按模型内置 |
| `AIASSISTANT_MODEL_THINK` | 思考开关 | 关 |
| `AIASSISTANT_MODEL_THINKING_LEVEL` | 思考强度 | 按配置 |
| `AIASSISTANT_MODEL_API_KEY` | 端点密钥（也可走加密库） | 空 |
| `AIASSISTANT_MODEL_ALLOW_REMOTE` | 允许公网端点 | 关 |

## 语音

| 变量 | 作用 | 默认 |
|---|---|---|
| `AIASSISTANT_ASR_ENGINE` | `sherpa` / `whisper` | `sherpa` |
| `AIASSISTANT_KWS_SCORE` | 唤醒得分阈值（0.1–10） | `3.0` |
| `AIASSISTANT_KWS_THRESHOLD` | 唤醒判定阈值（0.01–0.99） | `0.25` |
| `AIASSISTANT_VOICE_DISABLE_CHIME` | `true` 关闭提示音 | 有提示音 |
| `AIASSISTANT_VOICE_RECORD_COMMAND_AUDIO` | `true` 记录命令音频 | 关 |
| `AIASSISTANT_VOICE_AUDIO_RETENTION_DAYS` | 命令音频保留天数（仅在没有 UI 保存的 `retention.json` 时作为初始值） | `7` |

## 保留与容量

| 变量 | 作用 | 默认 |
|---|---|---|
| `AIASSISTANT_MEDIA_RETENTION_DAYS` | 加密媒体保留天数（同上，仅作初始值） | `30` |
| `AIASSISTANT_MAX_STORAGE_GB` | 数据目录容量上限（GB，`0` 表示不限；超限时从最旧媒体开始擦除） | `0` |

> 推荐在桌面端「数据与导出」页的「保留与清理」卡片中设置；保存后会写入 `<数据目录>/retention.json`，并优先于上述环境变量。

## 报告

| 变量 | 作用 | 默认 |
|---|---|---|
| `AIASSISTANT_REPORT_TIME` | 每日自动检查时间 `HH:mm`（仅在没有 UI 保存的 `report-schedule.json` 时作为初始值） | `00:30` |
| `AIASSISTANT_REPORT_DISABLE_SCHEDULE` | `true` 关闭自动检查（同上，仅作初始值） | 自动检查开启 |

> 推荐在桌面端「日报与站会」页的「自动日报」卡片中设置；保存后会写入 `<数据目录>/report-schedule.json`，并优先于上述环境变量。

## 本地 MCP

| 变量 | 作用 | 默认 |
|---|---|---|
| `AIASSISTANT_MCP_ENABLED` | `true` 启用只读 MCP | 关 |
| `AIASSISTANT_MCP_PORT` | 端口 | `47831` |
| `AIASSISTANT_MCP_RPM` | 每分钟请求上限 | `60` |

## UI

| 变量 | 作用 | 默认 |
|---|---|---|
| `AIASSISTANT_THEME` | `light` / `dark` | 跟随系统 |
| `AIASSISTANT_UI_E2E` | 启用进程内 UI 自驱动 | 关 |
| `AIASSISTANT_UI_E2E_SHOW` | 启动即显示窗口 | 关 |
| `AIASSISTANT_UI_E2E_OUTPUT` | E2E 截图/报告目录 | `artifacts/test-results/e2e` |

## 测试与诊断

| 变量 | 作用 | 默认 |
|---|---|---|
| `AIASSISTANT_SOAK_DAYS` | 多日加速浸泡天数 | `3` |
| `AIASSISTANT_REAL_SOAK_MINUTES` | 真实时钟浸泡分钟数（1–120） | 未设置时 Skip |
| `AIASSISTANT_MAC_HELPER_E2E` | macOS 真机 Helper 用例 | 关 |
| `AIASSISTANT_PORTAL_E2E` | Linux 真实 portal 用例 | 关 |
| `AIASSISTANT_LINUX_BACKEND` | 强制 `wayland` / `x11` | 自动 |
| `AIASSISTANT_WAYLAND_PIPEWIRE` | `false` 关闭 PipeWire | 开 |
| `AIASSISTANT_TEST_SECRET_*` | 测试密钥前缀（仅测试） | 无 |
