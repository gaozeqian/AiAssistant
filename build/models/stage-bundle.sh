#!/usr/bin/env bash
# 汇总随安装包分发的模型载荷（默认 artifacts/cache/models/bundle）。
# 只有对话与 Embedding 模型保持外置，本机语音栈随包。
#
# 期望的源布局（全部 gitignore，每台机器下载一次）：
#   artifacts/cache/models/sherpa-kws/kws/{encoder,decoder,joiner}.onnx, tokens.txt, keywords.txt
#   artifacts/cache/models/sherpa-onnx-paraformer-zh-small-2024-03-09/model.int8.onnx, tokens.txt
#   artifacts/cache/models/sherpa-tts/{model.onnx,tokens.txt,lexicon.txt,*.fst,dict/}  (MeloTTS, MIT)
#   artifacts/cache/models/sherpa-vad/silero_vad.onnx
# 可选：
#   artifacts/cache/models/rapidocr/{ch_PP-OCRv4_det_infer.onnx,ch_PP-OCRv4_rec_infer.onnx,ppocr_keys_v1.txt}
#   artifacts/cache/models/whisper/ggml-small.bin
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null || true)"
if [[ -z "$REPO_ROOT" ]]; then
  echo "错误：无法定位仓库根目录（$SCRIPT_DIR 不在 Git 工作区内）。" >&2
  exit 1
fi

usage() {
  cat <<'EOF'
用法: bash build/models/stage-bundle.sh [选项]

把本机语音模型汇总到 artifacts/cache/models/bundle，供三个平台的打包脚本复制进安装包。

选项:
  --with-rapidocr   额外打包 RapidOCR 模型（Linux OCR 兜底）
  --with-whisper    额外打包 Whisper ggml-small（高质量批处理 ASR）
  --dry-run         只检查源资产与目标布局，不写入任何文件
  -h, --help        显示本帮助

退出码: 0 成功 / 1 资产缺失或打包被拒绝 / 2 用法错误
EOF
}

log() { printf '[stage-bundle] %s\n' "$*"; }
die() { printf '[stage-bundle] 错误：%s\n' "$*" >&2; exit 1; }

ARTIFACTS_ROOT="${AIASSISTANT_ARTIFACTS_ROOT:-$REPO_ROOT/artifacts}"
MODELS="$ARTIFACTS_ROOT/cache/models"
BUNDLE="$MODELS/bundle"
WITH_RAPIDOCR=0
WITH_WHISPER=0
DRY_RUN=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --with-rapidocr) WITH_RAPIDOCR=1; shift ;;
    --with-whisper) WITH_WHISPER=1; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
  esac
done

KWS_SRC="$MODELS/sherpa-kws/kws"
ASR_SRC="$MODELS/sherpa-onnx-paraformer-zh-small-2024-03-09"
TTS_SRC="$MODELS/sherpa-tts"
VAD_SRC="$MODELS/sherpa-vad/silero_vad.onnx"

missing=()
[[ -f "$KWS_SRC/encoder.onnx" ]] || missing+=("$KWS_SRC/encoder.onnx (KWS 模型，见 docs/guides/models.md)")
[[ -f "$ASR_SRC/model.int8.onnx" ]] || missing+=("$ASR_SRC/model.int8.onnx (Paraformer ASR，见 docs/guides/models.md)")
[[ -f "$TTS_SRC/model.onnx" ]] || missing+=("$TTS_SRC/model.onnx (MeloTTS TTS，见 docs/guides/models.md)")
[[ -d "$TTS_SRC/dict" ]] || missing+=("$TTS_SRC/dict (MeloTTS 分词词典，见 docs/guides/models.md)")
for fst in date number phone new_heteronym; do
  [[ -f "$TTS_SRC/$fst.fst" ]] || missing+=("$TTS_SRC/$fst.fst (MeloTTS 文本正则化规则，见 docs/guides/models.md)")
done
[[ -f "$VAD_SRC" ]] || missing+=("$VAD_SRC (Silero VAD，见 docs/guides/models.md)")

if [[ ${#missing[@]} -gt 0 ]]; then
  echo "缺少随包分发的模型资产：" >&2
  printf '  - %s\n' "${missing[@]}" >&2
  exit 1
fi

if [[ "$DRY_RUN" -eq 1 ]]; then
  log "dry-run：将重建 $BUNDLE"
  log "  sherpa/kws        <- $KWS_SRC"
  log "  sherpa           <- $ASR_SRC/model.int8.onnx + tokens.txt"
  log "  sherpa/tts       <- $TTS_SRC（含 dict/ 与 4 个 .fst）"
  log "  sherpa/silero_vad.onnx <- $VAD_SRC"
  [[ "$WITH_RAPIDOCR" -eq 1 ]] && log "  rapidocr         <- $MODELS/rapidocr（缺失时跳过）"
  [[ "$WITH_WHISPER" -eq 1 ]] && log "  whisper          <- $MODELS/whisper/ggml-small.bin（缺失时跳过）"
  log "dry-run 结束，未写入任何文件。"
  exit 0
fi

rm -rf "$BUNDLE"
mkdir -p "$BUNDLE/sherpa/kws" "$BUNDLE/sherpa/tts/dict"

cp "$KWS_SRC/encoder.onnx" "$KWS_SRC/decoder.onnx" "$KWS_SRC/joiner.onnx" \
   "$KWS_SRC/tokens.txt" "$KWS_SRC/keywords.txt" "$BUNDLE/sherpa/kws/"
cp "$ASR_SRC/model.int8.onnx" "$ASR_SRC/tokens.txt" "$BUNDLE/sherpa/"
cp "$VAD_SRC" "$BUNDLE/sherpa/silero_vad.onnx"
cp "$TTS_SRC/model.onnx" "$TTS_SRC/tokens.txt" "$TTS_SRC/lexicon.txt" "$BUNDLE/sherpa/tts/"
cp "$TTS_SRC"/*.fst "$BUNDLE/sherpa/tts/"
cp -R "$TTS_SRC/dict/." "$BUNDLE/sherpa/tts/dict/"
if [[ -f "$TTS_SRC/LICENSE" ]]; then
  cp "$TTS_SRC/LICENSE" "$BUNDLE/sherpa/tts/LICENSE"
fi

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

if find "$BUNDLE" -iname '*aishell3*' | grep -q .; then
  die "拒绝打包：bundle 中出现已退役的 AISHELL-3 权重（许可证不明，见 docs/release/compliance.md）"
fi

log "模型包已就绪：$BUNDLE ($(du -sh "$BUNDLE" | cut -f1))"
find "$BUNDLE" -type f | sed "s|$BUNDLE/||" | sort | sed 's/^/  /'
