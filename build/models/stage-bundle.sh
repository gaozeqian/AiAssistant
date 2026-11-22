#!/usr/bin/env bash
# Assembles the model payload that ships inside installers (artifacts/models/bundle).
# Only chat/embedding models stay external; the local voice stack is bundled.
#
# Expected source layout (all gitignored, download once per machine):
#   artifacts/models/sherpa-kws/kws/{encoder,decoder,joiner}.onnx, tokens.txt, keywords.txt
#   artifacts/models/sherpa-onnx-paraformer-zh-small-2024-03-09/model.int8.onnx, tokens.txt
#   artifacts/models/sherpa-tts/{model.onnx,tokens.txt,lexicon.txt}
#   artifacts/models/sherpa-vad/silero_vad.onnx
# Optional:
#   artifacts/models/rapidocr/{ch_PP-OCRv4_det_infer.onnx,ch_PP-OCRv4_rec_infer.onnx,ppocr_keys_v1.txt}
#   artifacts/models/whisper/ggml-small.bin
#
# Usage: bash build/models/stage-bundle.sh [--with-rapidocr] [--with-whisper]
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
MODELS="$REPO_ROOT/artifacts/models"
BUNDLE="$MODELS/bundle"
WITH_RAPIDOCR=0
WITH_WHISPER=0

for arg in "$@"; do
  case "$arg" in
    --with-rapidocr) WITH_RAPIDOCR=1 ;;
    --with-whisper) WITH_WHISPER=1 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

KWS_SRC="$MODELS/sherpa-kws/kws"
ASR_SRC="$MODELS/sherpa-onnx-paraformer-zh-small-2024-03-09"
TTS_SRC="$MODELS/sherpa-tts"
VAD_SRC="$MODELS/sherpa-vad/silero_vad.onnx"

missing=()
[[ -f "$KWS_SRC/encoder.onnx" ]] || missing+=("$KWS_SRC/encoder.onnx (KWS 模型，见 docs/MODELS.md)")
[[ -f "$ASR_SRC/model.int8.onnx" ]] || missing+=("$ASR_SRC/model.int8.onnx (Paraformer ASR，见 docs/MODELS.md)")
[[ -f "$TTS_SRC/model.onnx" ]] || missing+=("$TTS_SRC/model.onnx (VITS TTS，见 docs/MODELS.md)")
[[ -f "$VAD_SRC" ]] || missing+=("$VAD_SRC (Silero VAD，见 docs/MODELS.md)")

if [[ ${#missing[@]} -gt 0 ]]; then
  echo "缺少随包分发的模型资产：" >&2
  printf '  - %s\n' "${missing[@]}" >&2
  exit 1
fi

rm -rf "$BUNDLE"
mkdir -p "$BUNDLE/sherpa/kws" "$BUNDLE/sherpa/tts"

cp "$KWS_SRC/encoder.onnx" "$KWS_SRC/decoder.onnx" "$KWS_SRC/joiner.onnx" \
   "$KWS_SRC/tokens.txt" "$KWS_SRC/keywords.txt" "$BUNDLE/sherpa/kws/"
cp "$ASR_SRC/model.int8.onnx" "$ASR_SRC/tokens.txt" "$BUNDLE/sherpa/"
cp "$VAD_SRC" "$BUNDLE/sherpa/silero_vad.onnx"
cp "$TTS_SRC/model.onnx" "$TTS_SRC/tokens.txt" "$TTS_SRC/lexicon.txt" "$BUNDLE/sherpa/tts/"

if [[ "$WITH_RAPIDOCR" -eq 1 ]]; then
  if [[ -f "$MODELS/rapidocr/ch_PP-OCRv4_det_infer.onnx" ]]; then
    mkdir -p "$BUNDLE/rapidocr"
    cp "$MODELS"/rapidocr/*.onnx "$MODELS"/rapidocr/ppocr_keys_v1.txt "$BUNDLE/rapidocr/"
  else
    echo "warning: --with-rapidocr set but $MODELS/rapidocr is incomplete; skipping" >&2
  fi
fi

if [[ "$WITH_WHISPER" -eq 1 ]]; then
  if [[ -f "$MODELS/whisper/ggml-small.bin" ]]; then
    mkdir -p "$BUNDLE/whisper"
    cp "$MODELS/whisper/ggml-small.bin" "$BUNDLE/whisper/"
  else
    echo "warning: --with-whisper set but $MODELS/whisper/ggml-small.bin is missing; skipping" >&2
  fi
fi

echo "模型包已就绪：$BUNDLE ($(du -sh "$BUNDLE" | cut -f1))"
find "$BUNDLE" -type f | sed "s|$BUNDLE/||" | sort | sed 's/^/  /'
