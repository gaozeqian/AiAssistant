#!/usr/bin/env bash
# 把仓库内已下载的 sherpa-onnx 语音模型安装到应用数据目录，使 GUI 的语音唤醒/命令识别就绪，
# 无需设置 AIASSISTANT_SHERPA_MODEL_DIR 环境变量。
#
# 用法：
#   bash build/models/install-voice-models.sh [目标数据目录]
# 默认目标：${AIASSISTANT_DATA_DIRECTORY:-$HOME/.local/share/AiAssistant}
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
KWS_SRC="$REPO_ROOT/artifacts/models/sherpa-kws/kws"
ASR_SRC="$REPO_ROOT/artifacts/models/sherpa-onnx-paraformer-zh-small-2024-03-09"

for path in "$KWS_SRC" "$ASR_SRC"; do
  [[ -d "$path" ]] || { echo "缺少模型资产目录：$path（请先下载 sherpa-onnx KWS / Paraformer 模型）" >&2; exit 1; }
done

DEST="${1:-${AIASSISTANT_DATA_DIRECTORY:-$HOME/.local/share/AiAssistant}}/models/sherpa"
mkdir -p "$DEST/kws"

cp "$KWS_SRC/"decoder.onnx "$KWS_SRC/"encoder.onnx "$KWS_SRC/"joiner.onnx \
   "$KWS_SRC/"tokens.txt "$KWS_SRC/"keywords.txt "$DEST/kws/"
cp "$ASR_SRC/model.int8.onnx" "$DEST/model.int8.onnx"
cp "$ASR_SRC/tokens.txt" "$DEST/tokens.txt"

echo "语音模型已安装到：$DEST"
echo "  - KWS（你好时光）：$DEST/kws/"
echo "  - 命令 ASR（Paraformer zh）：$DEST/model.int8.onnx"
echo "下一步：在语音页点「启用语音」，状态应变为「正在聆听」。"
