# 本机模型配置与语音模型安装

> **状态**：living ｜ **最后更新**：2026-11-27 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：默认模型、端点策略、语音模型安装或 UI 联调方式变化时

应用默认只使用本机端点（`http://127.0.0.1:11434`），不向云端发送数据。所有端点统一走 **OpenAI 兼容协议**（`/v1/chat/completions`、`/v1/embeddings`、`/v1/models`）：Ollama、LM Studio、vLLM 与云厂商只差地址、模型名和密钥，没有 provider 开关。配置键权威清单见 [配置参考](../reference/configuration.md)。

## 1. 对话 / 向量模型

Ollama 自带 OpenAI 兼容端点，默认运行时不需额外适配：

```bash
ollama list
curl http://127.0.0.1:11434/v1/models
```

| 用途 | 默认模型 |
|---|---|
| 对话（问答、日报摘要） | `gemma4:12b` |
| 向量（检索 embed） | `embeddinggemma-2:740m`（768 维） |

名称不同时用环境变量覆盖：

```bash
export AIASSISTANT_CHAT_MODEL=gemma4:12b-mlx
export AIASSISTANT_EMBEDDING_MODEL=embeddinggemma-2:740m
```

验证：

```bash
dotnet run --project src/AiAssistant.Host/AiAssistant.Host.csproj -- --probe-model
dotnet run --project src/AiAssistant.Host/AiAssistant.Host.csproj -- --benchmark-model
```

- 更换 embedding 模型或文档格式后执行 `--rebuild-vectors`；采集后直接问答会自动补建索引。
- 推理型模型默认向本机端点发送 `reasoning_effort: "none"`，避免耗尽输出预算；可在诊断页「模型配置」开启思考并选择强度（`low`/`medium`/`high`），或设置 `AIASSISTANT_MODEL_THINK` / `AIASSISTANT_MODEL_THINKING_LEVEL`。远程端点仅在显式开启思考时发送该字段，避免不支持它的厂商返回 HTTP 400。
- loopback 端点显式绕过系统代理，避免公司代理导致本机模型调用失败。

## 2. 语音模型安装

语音模型（sherpa-onnx）不进入 Git。把下载好的资产安装到应用数据目录：

```bash
bash tools/install-voice-models.sh
# 或指定目录
bash tools/install-voice-models.sh /path/to/data-directory
```

默认数据目录按 OS 解析：

- macOS：`~/Library/Application Support/AiAssistant`
- Linux：`${XDG_DATA_HOME:-$HOME/.local/share}/AiAssistant`

安装后布局 `<数据目录>/models/sherpa/`：

```text
kws/  encoder.onnx decoder.onnx joiner.onnx tokens.txt keywords.txt
model.int8.onnx  tokens.txt  silero_vad.onnx
tts/  model.onnx tokens.txt lexicon.txt *.fst dict/
```

`keywords.txt` 每行一个唤醒词，例如 `n ǐ h ǎo sh í g uāng @你好时光`。安装后无需设置 `AIASSISTANT_SHERPA_MODEL_DIR`；也可显式指向外部模型根目录。

## 3. 随包模型

安装包携带 `artifacts/cache/models/bundle`（KWS、Paraformer ASR、MeloTTS TTS、Silero VAD、RapidOCR），应用解析顺序：

1. `AIASSISTANT_SHERPA_MODEL_DIR`（语音）/ `AIASSISTANT_RAPIDOCR_MODEL_DIR`（OCR）
2. `<数据目录>/models/sherpa`、`<数据目录>/models/rapidocr`
3. `<应用目录>/models/sherpa`、`<应用目录>/models/rapidocr`

| 平台 | 随包位置 |
|---|---|
| Windows zip/MSI | `<安装目录>\models\{sherpa,rapidocr}` |
| macOS `.app` | `AiAssistant.app/Contents/MacOS/models/{sherpa,rapidocr}` |
| Linux deb/tar | `/usr/lib/aiassistant/models/{sherpa,rapidocr}` |

外部仍需准备对话/Embedding 模型（Ollama 或内网/公网端点）。随包资产来源与许可证见 [合规](../release/compliance.md)。

## 4. OCR 模型（图像文字识别兜底）

无障碍文本为空时，Windows 与 Linux 会尝试本地 RapidOCR（PP-OCRv4）。安装包默认携带该资产，诊断页「OCR」应显示为可用；如需替换或源码运行时手动安装，放到下列任一目录：

1. `AIASSISTANT_RAPIDOCR_MODEL_DIR`
2. `<数据目录>/models/rapidocr`
3. `<应用目录>/models/rapidocr`

```text
ch_PP-OCRv4_det_infer.onnx
ch_PP-OCRv4_rec_infer.onnx
ppocr_keys_v1.txt
```

资产缺失时诊断页显示为降级并给出安装提示，安装后点「重新检查」即可变为可用。`bash build/models/fetch-bundle.sh` 会下载并校验上述文件，`stage-bundle.sh` 将其汇总进安装包。

## 5. 从 UI 配置模型

诊断页「模型配置」可独立设置对话与向量端点：来源（本机/内网/公网）、模型名、Base URL、接口密钥、思考开关与强度。地址指向任意 OpenAI 兼容运行时即可（Ollama、LM Studio、vLLM 或云厂商）。

- 两个模型完全独立：同档位也可以使用不同地址、不同模型与不同密钥。
- 本机端点必须 loopback；内网/公网允许远程地址，失败不会自动回落到本机。
- 接口密钥在对应端点输入框填写，写入加密库（名称固定为 `chat-endpoint-key` / `embedding-endpoint-key`），`models.json` 只保存 `apiKeySecret` 引用；勾选「清除已保存密钥」可删除。
- 保存写入 `<数据目录>/models.json`，只保留当前在用的对话与向量模型（schema 3），重启生效。
- 也可用 `--import-model-secret <名称>` 预先写入密钥，再在 `apiKeySecret` 中引用该名称。

## 6. Linux 桌面图标与托盘

```bash
bash tools/install-desktop-entry.sh artifacts/publish/linux-x64/aiassistant
```

- Wayland `app_id=aiassistant`；X11 `StartupWMClass=aiassistant`。
- 无托盘环境关闭窗口即退出；有托盘时关闭仅隐藏，再次启动通过 `<数据目录>/instance.sock` 唤回窗口。

## 7. macOS 真实 UI 联调

`tools/ui-e2e/ollama-flow.swift` 在真实 `.app` 上驱动语音 → 检索 → 问答 → 日报：

```bash
open -n artifacts/staging/osx-arm64-app/AiAssistant.app --args --show-window
swift tools/ui-e2e/ollama-flow.swift
```

宿主进程需授予辅助功能与屏幕录制权限。进程内自驱动可用 `tools/AiAssistant.Diagnostics.Host --ui-e2e`，截图与报告写入 `artifacts/test-results/e2e/`。
