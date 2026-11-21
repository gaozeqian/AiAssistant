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
