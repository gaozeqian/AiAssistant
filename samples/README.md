# samples/ — 示例配置

本目录放可直接复制使用的示例配置，不含任何真实密钥或生产端点。

| 文件 | 用途 |
|---|---|
| `models.example.json` | `models.json` 示例：三档端点、能力探测、策略开关 |

## 使用

把示例复制到数据目录并改名为 `models.json`：

| 平台 | 数据目录 |
|---|---|
| Windows | `%LOCALAPPDATA%\AiAssistant` |
| macOS | `~/Library/Application Support/AiAssistant` |
| Linux | `~/.local/share/AiAssistant` |

或通过 `AIASSISTANT_MODELS_CONFIG` 指定任意路径。配置键与环境变量的权威清单见 [docs/reference/configuration.md](../docs/reference/configuration.md) 与 [docs/reference/environment-variables.md](../docs/reference/environment-variables.md)。

示例中的 API Key 一律用 `env:VAR_NAME` 引用或走加密密钥库（`--import-model-secret`），**禁止**把明文密钥写进本目录。
