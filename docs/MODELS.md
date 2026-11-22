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
> （`ModelGatewayOptions.DisableThinking`，无思考能力的模型会忽略该参数）；实测
> `--benchmark-model` 由 67%/17.4s 提升到 100%/0.86s。

## 2. 语音唤醒 / 命令识别模型

语音模型（sherpa-onnx）作为外部资产不进 Git。仓库 `artifacts/models/` 下已有下载好的资产。用安装脚本装到应用数据目录：

```bash
bash build/install-voice-models.sh
# 或指定数据目录：
bash build/install-voice-models.sh /path/to/data-directory
```

脚本把两类模型放到 `<数据目录>/models/sherpa/`：

- `kws/`：唤醒词「你好时光」的 encoder / decoder / joiner / tokens / keywords
- 根目录：`model.int8.onnx` + `tokens.txt`：命令识别 Paraformer（中文）

安装后无需设置 `AIASSISTANT_SHERPA_MODEL_DIR`。若想用外部目录，也可直接设该环境变量指向包含上述结构的根目录。

验证：启动 GUI → 语音页，能力状态应从「Wake-word model assets are not installed」变为
「Wake word ready」（若 TTS 资产未装会追加 `TTS feedback unavailable`，不影响唤醒与识别）。

## 2.5 从 UI 配置模型

诊断页「模型配置」内可**独立**配置对话模型与 Embedding 模型，各自有：

- **模型来源**：本机 Ollama / 企业内网 / 公网 API
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
- GNOME 默认没有 StatusNotifierItem 托盘：此时窗口关闭即退出应用（顶部也有「退出」按钮），
  不会再出现「隐藏后无法找回」的窗口。

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
