#!/usr/bin/env bash
# 把仓库内已下载的 sherpa-onnx 语音模型安装到应用数据目录，
# 使 GUI 的语音唤醒/命令识别就绪，无需设置 AIASSISTANT_SHERPA_MODEL_DIR。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null || true)"
if [[ -z "$REPO_ROOT" ]]; then
  echo "错误：无法定位仓库根目录（$SCRIPT_DIR 不在 Git 工作区内）。" >&2
  exit 1
fi

usage() {
  cat <<'EOF'
用法: bash tools/install-voice-models.sh [目标数据目录] [选项]

把 artifacts/models 下已下载的 KWS / Paraformer / MeloTTS 资产复制到
<目标数据目录>/models/sherpa，供本机 GUI 直接使用。

  [目标数据目录]   默认 $AIASSISTANT_DATA_DIRECTORY，否则 ~/.local/share/AiAssistant

选项:
  --dry-run        只打印将要复制的文件，不写入
  -h, --help       显示本帮助

退出码: 0 成功 / 1 模型资产缺失 / 2 用法错误
EOF
}

DEST_ROOT=""
DRY_RUN=0
for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    -h|--help) usage; exit 0 ;;
    -*) usage >&2; exit 2 ;;
    *)
      if [[ -n "$DEST_ROOT" ]]; then usage >&2; exit 2; fi
      DEST_ROOT="$arg"
      ;;
  esac
done

KWS_SRC="$REPO_ROOT/artifacts/models/sherpa-kws/kws"
ASR_SRC="$REPO_ROOT/artifacts/models/sherpa-onnx-paraformer-zh-small-2024-03-09"
TTS_SRC="$REPO_ROOT/artifacts/models/sherpa-tts"

for path in "$KWS_SRC" "$ASR_SRC"; do
  [[ -d "$path" ]] || { echo "缺少模型资产目录：$path（请先下载 sherpa-onnx KWS / Paraformer 模型）" >&2; exit 1; }
done

DEST="${DEST_ROOT:-${AIASSISTANT_DATA_DIRECTORY:-$HOME/.local/share/AiAssistant}}/models/sherpa"

if [[ "$DRY_RUN" -eq 1 ]]; then
  echo "dry-run：将安装到 $DEST"
  echo "  - KWS（你好时光）：$KWS_SRC -> $DEST/kws/"
  echo "  - 命令 ASR（Paraformer zh）：$ASR_SRC/model.int8.onnx -> $DEST/"
  if [[ -f "$TTS_SRC/model.onnx" ]]; then
    echo "  - TTS（MeloTTS zh_en）：$TTS_SRC -> $DEST/tts/"
  fi
  echo "dry-run 结束，未写入任何文件。"
  exit 0
fi

mkdir -p "$DEST/kws"

cp "$KWS_SRC/"decoder.onnx "$KWS_SRC/"encoder.onnx "$KWS_SRC/"joiner.onnx \
   "$KWS_SRC/"tokens.txt "$KWS_SRC/"keywords.txt "$DEST/kws/"
cp "$ASR_SRC/model.int8.onnx" "$DEST/model.int8.onnx"
cp "$ASR_SRC/tokens.txt" "$DEST/tokens.txt"

if [[ -f "$TTS_SRC/model.onnx" ]]; then
  mkdir -p "$DEST/tts/dict"
  cp "$TTS_SRC/model.onnx" "$TTS_SRC/tokens.txt" "$TTS_SRC/lexicon.txt" "$DEST/tts/"
  cp "$TTS_SRC"/*.fst "$DEST/tts/"
  cp -R "$TTS_SRC/dict/." "$DEST/tts/dict/"
  if [[ -f "$TTS_SRC/LICENSE" ]]; then
    cp "$TTS_SRC/LICENSE" "$DEST/tts/LICENSE"
  fi
fi

echo "语音模型已安装到：$DEST"
echo "  - KWS（你好时光）：$DEST/kws/"
echo "  - 命令 ASR（Paraformer zh）：$DEST/model.int8.onnx"
if [[ -f "$DEST/tts/model.onnx" ]]; then
  echo "  - TTS（MeloTTS zh_en）：$DEST/tts/"
fi
echo "下一步：在语音页点「启用语音」，状态应变为「正在聆听」。"
