#!/usr/bin/env bash
# Downloads the official sherpa-onnx model assets that ship inside installers and stages
# artifacts/models/bundle. Only chat/embedding models stay external.
#
#   bash build/models/fetch-bundle.sh [--with-rapidocr] [--with-whisper]
#
# Assets (all Apache-2.0 upstream unless noted):
#   KWS   kws-models/sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01.tar.bz2
#   ASR   asr-models/sherpa-onnx-paraformer-zh-small-2024-03-09.tar.bz2
#   TTS   tts-models/vits-zh-aishell3.tar.bz2
#   VAD   asr-models/silero_vad_v5.onnx
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
MODELS="$REPO_ROOT/artifacts/models"
CACHE="$MODELS/downloads"
REPO="k2-fsa/sherpa-onnx"
mkdir -p "$CACHE"

fetch() {
  local tag="$1" pattern="$2"
  local target="$CACHE/$pattern"
  if [[ -f "$target" ]]; then
    echo "cached: $pattern"
    return
  fi

  echo "fetching: $pattern"
  if command -v gh >/dev/null 2>&1 && gh auth status >/dev/null 2>&1; then
    gh release download "$tag" --repo "$REPO" --pattern "$pattern" --dir "$CACHE" --clobber
  else
    curl -fL --retry 3 --retry-delay 2 \
      "https://github.com/$REPO/releases/download/$tag/$pattern" -o "$target"
  fi
}

extract() {
  local archive="$1" dest="$2"
  mkdir -p "$dest"
  tar -xjf "$archive" -C "$dest"
}

fetch kws-models sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01.tar.bz2
fetch asr-models sherpa-onnx-paraformer-zh-small-2024-03-09.tar.bz2
fetch tts-models vits-zh-aishell3.tar.bz2
fetch asr-models silero_vad_v5.onnx

KWS_ROOT="$CACHE/kws-extract"
ASR_ROOT="$CACHE/asr-extract"
TTS_ROOT="$CACHE/tts-extract"
rm -rf "$KWS_ROOT" "$ASR_ROOT" "$TTS_ROOT"
mkdir -p "$KWS_ROOT" "$ASR_ROOT" "$TTS_ROOT"

extract "$CACHE/sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01.tar.bz2" "$KWS_ROOT"
extract "$CACHE/sherpa-onnx-paraformer-zh-small-2024-03-09.tar.bz2" "$ASR_ROOT"
extract "$CACHE/vits-zh-aishell3.tar.bz2" "$TTS_ROOT"

KWS_SRC="$(find "$KWS_ROOT" -type d -name 'sherpa-onnx-kws-*' | head -1)"
ASR_SRC="$(find "$ASR_ROOT" -type d -name 'sherpa-onnx-paraformer-*' | head -1)"
TTS_MODEL="$(find "$TTS_ROOT" -name 'vits-aishell3.int8.onnx' | head -1)"
if [[ -z "$TTS_MODEL" ]]; then
  TTS_MODEL="$(find "$TTS_ROOT" -name '*.onnx' | head -1)"
fi
TTS_DIR="$(dirname "$TTS_MODEL")"

mkdir -p "$MODELS/sherpa-kws/kws" "$MODELS/sherpa-tts" "$MODELS/sherpa-vad"
cp "$KWS_SRC/encoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx" "$MODELS/sherpa-kws/kws/encoder.onnx"
cp "$KWS_SRC/decoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx" "$MODELS/sherpa-kws/kws/decoder.onnx"
cp "$KWS_SRC/joiner-epoch-12-avg-2-chunk-16-left-64.int8.onnx" "$MODELS/sherpa-kws/kws/joiner.onnx"
cp "$KWS_SRC/tokens.txt" "$MODELS/sherpa-kws/kws/tokens.txt"
printf 'n ǐ h ǎo sh í g uāng @你好时光\n' > "$MODELS/sherpa-kws/kws/keywords.txt"

rm -rf "$MODELS/sherpa-onnx-paraformer-zh-small-2024-03-09"
cp -R "$ASR_SRC" "$MODELS/sherpa-onnx-paraformer-zh-small-2024-03-09"

cp "$TTS_MODEL" "$MODELS/sherpa-tts/model.onnx"
cp "$TTS_DIR/tokens.txt" "$TTS_DIR/lexicon.txt" "$MODELS/sherpa-tts/"
cp "$CACHE/silero_vad_v5.onnx" "$MODELS/sherpa-vad/silero_vad.onnx"

bash "$SCRIPT_DIR/stage-bundle.sh" "$@"
