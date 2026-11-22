# 本机模型配置与语音模型安装

应用默认使用本机 Ollama（`http://127.0.0.1:11434`），不向云端发送任何数据。

## 1. Ollama 对话 / 向量模型

确认本机已安装并启动 Ollama：

```bash
ollama list        # 查看已安装模型
curl http://127.0.0.1:11434/api/tags
```

默认模型名：

| 用途 | 默认名 | 说明 |
|---|---|---|
| 对话（问答、日报摘要） | `gemma4:12b` | 若本机装的是 MLX 变体，需显式指定 |
| 向量（检索 embed） | `embeddinggemma-2:740m` | 768 维，官方查询前缀 |

如模型名与默认不同，用环境变量指定（以本机实际安装的 `gemma4:12b-mlx` 为例）：

```bash
export AIASSISTANT_CHAT_MODEL=gemma4:12b-mlx
export AIASSISTANT_EMBEDDING_MODEL=embeddinggemma-2:740m
```

验证接入：

```bash
AiAssistant.Host --probe-model                # chat=True embed=True 即正常
AiAssistant.Host --benchmark-model            # 对话基准（退出码非 0 表示失败）
```

> 换 embedding 模型后需重建索引：`AiAssistant.Host --rebuild-vectors`。
> 采集后直接问答会自动补建索引（无需手动 rebuild）。
>
> **推理型模型**（如带 `thinking` 能力的 `gemma4`）默认会先思考再回答，容易耗尽输出预算，
> 表现为结构化日报为空或单次调用 20–60 秒。应用默认在 Ollama 请求中发送 `think: false`
> （`ModelGatewayOptions.Think`，无思考能力的模型会忽略该参数）；实测 `--benchmark-model`
> 由 67%/17.4s 提升到 100%/0.86s。
>
> 需要思考时可在诊断页「模型配置」勾选「允许思考」并选择思考强度（低/中/高），保存后写入
> `models.json` 的 `think` / `thinkingLevel`（对应 Ollama 的 `think: true|low|medium|high`）；
> 也可用 `AIASSISTANT_MODEL_THINK=true` / `AIASSISTANT_MODEL_THINKING_LEVEL=high` 覆盖。

## 2. 语音唤醒 / 命令识别模型

语音模型（sherpa-onnx）作为外部资产不进 Git。仓库 `artifacts/models/` 下已有下载好的资产。用安装脚本装到应用数据目录：

```bash
bash build/install-voice-models.sh
# 或指定数据目录：
bash build/install-voice-models.sh /path/to/data-directory
```

脚本把语音模型放到 `<数据目录>/models/sherpa/`：

- `kws/`：唤醒词「你好时光」的 encoder / decoder / joiner / tokens / keywords
- 根目录：`model.int8.onnx` + `tokens.txt`：命令识别 Paraformer（中文）
- `tts/`（可选）：`model.onnx` + `tokens.txt` + `lexicon.txt` + `dict/` + `date.fst` / `number.fst` / `phone.fst` / `new_heteronym.fst`：MeloTTS 语音合成（中英混读，MIT）

安装后无需设置 `AIASSISTANT_SHERPA_MODEL_DIR`。若想用外部目录，也可直接设该环境变量指向包含上述结构的根目录。

验证：启动 GUI → 语音页，能力状态应从「Wake-word model assets are not installed」变为
「Wake word ready」（若 TTS 资产未装会追加 `TTS feedback unavailable`，不影响唤醒与识别）。

### 2.1 安装包载荷：只有对话/Embedding 模型在外部（2026-11-22 实测）

安装包携带 `artifacts/models/bundle`（KWS 唤醒词、Paraformer ASR、MeloTTS TTS、Silero VAD，约 268MB），放在应用目录 `models/sherpa`。**只有对话与 Embedding 模型保持外部**（Ollama / 内网 / 公网）。

| 平台 | 随包模型位置 |
|---|---|
| Windows zip / MSI | `<安装目录>\models\sherpa` |
| macOS `.app` | `AiAssistant.app/Contents/MacOS/models/sherpa` |
| Linux tar.gz / deb | `/usr/lib/aiassistant/models/sherpa` |

应用解析顺序：`AIASSISTANT_SHERPA_MODEL_DIR` → `<数据目录>/models/sherpa` → `<应用目录>/models/sherpa`。
数据目录存在资产时优先，用户仍可覆盖随包模型；两个目录都没有时保持原有降级提示。

**仍需外部准备**

- Ollama 与 `gemma4:12b`（约 7.1GB）+ `embeddinggemma-2:740m`（约 1.2GB），或配置内网/公网端点。
- 可选资产：Whisper `ggml-small.bin`（466MB，`--with-whisper` 打包）、RapidOCR（Linux 图像 OCR 兜底，`--with-rapidocr`）、说话人分离 `segmentation.onnx` + `speaker-embedding.onnx`（实验特性，默认不打包）。
- 麦克风权限：Windows「设置 → 隐私 → 麦克风 → 允许桌面应用」；macOS 首次运行 TCC 授权。

**平台门槛**

- Windows：无需 .NET 运行时；发布产物全量扫描无 `MSVCP140`/`VCRUNTIME140` 导入，**不需要 VC++ Redistributable**。发布格式只有自包含 zip 与 MSI；MSI 为 `perMachine`，安装需管理员；未签名包会被 SmartScreen/证书信任拦截。数据目录 `%LOCALAPPDATA%\AiAssistant`，卸载保留。PDF 可检索依赖系统 CJK 字体。
- macOS：默认 `osx-arm64`、最低 13.0；干净机器必须 Developer ID 签名 + 公证。首次运行需授予屏幕录制、辅助功能、麦克风。Swift Helper 必须随 `.app` 打入。Intel Mac 需另出 `osx-x64`/universal 包。
- Linux：deb 声明依赖 `libsecret-1-0, libx11-6, libice6, libsm6, libfontconfig1`；运行期还需要 `libpulse-simple.so.0`、`xdg-desktop-portal` + `pipewire`（Wayland 抓屏）与 CJK 字体（PDF）。GNOME 默认无托盘，关闭只隐藏、用顶部「退出」。

**开发机手动覆盖（可选）**

```text
<数据目录>/models/sherpa/
  kws/  encoder.onnx  decoder.onnx  joiner.onnx  tokens.txt  keywords.txt
  model.int8.onnx  tokens.txt  silero_vad.onnx
  tts/  model.onnx  tokens.txt  lexicon.txt  date.fst  number.fst  phone.fst  new_heteronym.fst
  tts/dict/  （MeloTTS 分词词典）
```

`keywords.txt` 一行一个唤醒词：`n ǐ h ǎo sh í g uāng @你好时光`。
打包前用 `bash build/models/stage-bundle.sh` 汇总资产（或 `fetch-bundle.sh` 直接下载官方模型）。
随包资产来源与许可证见 `docs/compliance/MODEL-ASSETS.md`（全部为 Apache-2.0 / MIT，已移除许可不明的 AISHELL-3 VITS）；`THIRD-PARTY-NOTICES.md` 已生成并随安装包分发。

## 2.5 从 UI 配置模型

诊断页「模型配置」内可**独立**配置对话模型与 Embedding 模型，各自有：

- **模型来源**：本机 / 企业内网 / 公网 API（「本机」不绑定具体运行时：保存时保留已有 provider，
  新端点自动探测 Ollama `/api/tags`，否则按 OpenAI 兼容接口处理，可对接 LM Studio / vLLM 等）
- **模型名**（例如 `gemma4:12b-mlx` / `embeddinggemma-2:740m`）
- **Base URL**（本机默认 `http://127.0.0.1:11434`；外部模型可填远程地址）

对话与 Embedding 可指向不同端点（如 chat 在本地、embedding 在内网，或两个内网地址）。点「保存模型配置」写入数据目录的 `models.json`：

- 对话端点写入 `endpoints`（按 tier），Embedding 端点写入独立的 `embedding` 文档（同 tier 时省略、复用对话端点）。
- 本机（LocalOffline）端点必须使用 loopback；内网/公网允许远程地址并自动标记 `allowRemoteEndpoint`。
- 保存后把 `defaultTier` 切到对话模型所选层；选择公网时同时放开 `allowPublicCloud`（选择公网即视为显式同意）。
- **内网/公网故障不会自动回落到本机**，会在界面提示「模型不可用」。
- 始终保留 LocalOffline 兜底端点。保存后**重启应用生效**。

## 2.7 Linux 桌面图标与托盘

Wayland 下窗口图标由合成器按 `app_id` 匹配 `<app_id>.desktop` 解析（应用已设置
`app_id=aiassistant`）；X11 使用 `WM_CLASS=AiAssistant.Host`。安装用户级 desktop 条目与图标：

```bash
bash build/install-desktop-entry.sh          # 默认指向 Release 构建产物
```

- 写入 `~/.local/share/applications/aiassistant.desktop` 与
  `~/.local/share/icons/hicolor/256x256/apps/aiassistant.png`（`Icon=aiassistant`）。
- 点击窗口关闭按钮只隐藏窗口，采集继续在后台运行；退出需显式点击顶部「退出」按钮（或托盘菜单）。
- 再次启动（Dock 图标 / desktop 条目）会通过 `<数据目录>/instance.sock` 通知已运行实例显示窗口，
  不会重复启动第二个采集进程。GNOME 默认无 StatusNotifierItem 托盘时，启动会直接显示主窗口。

## 3. 完整流程 UI 测试

`tests/ui-e2e/ollama-flow.swift` 在真实 `.app` 上按真人操作跑完整链路：
语音就绪 → 检索（embed）→ 问答（chat）→ 日报生成。

前置：宿主进程已授予「辅助功能」+「屏幕与系统音频录制」；用模型环境变量启动应用：

```bash
AIASSISTANT_DATA_DIRECTORY=<装好模型的数据目录> \
AIASSISTANT_CHAT_MODEL=gemma4:12b-mlx \
AIASSISTANT_EMBEDDING_MODEL=embeddinggemma-2:740m \
  open -n artifacts/packages/staging/osx-arm64/AiAssistant.app --args --show-window

swift tests/ui-e2e/ollama-flow.swift
```

脚本复用已运行实例，用 OCR 定位按钮、真实鼠标点击、剪贴板输入问题，并用 AX 读取可见文本断言。
