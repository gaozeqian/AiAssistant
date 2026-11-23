#!/usr/bin/env bash
# 下载随安装包分发的 sherpa-onnx 官方模型资产，并汇总到 artifacts/cache/models/bundle。
# 只有对话与 Embedding 模型保持外置。
#
# 资产（上游均为 Apache-2.0 或 MIT）：
#   KWS   kws-models/sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01.tar.bz2
#   ASR   asr-models/sherpa-onnx-paraformer-zh-small-2024-03-09.tar.bz2   (Apache-2.0)
#   TTS   tts-models/vits-melo-tts-zh_en.tar.bz2                          (MeloTTS, MIT)
#   VAD   asr-models/silero_vad_v5.onnx
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null || true)"
if [[ -z "$REPO_ROOT" ]]; then
  echo "错误：无法定位仓库根目录（$SCRIPT_DIR 不在 Git 工作区内）。" >&2
  exit 1
fi

usage() {
  cat <<'EOF'
用法: bash build/models/fetch-bundle.sh [选项]

下载并展开 sherpa-onnx 模型资产到 artifacts/cache/models/，然后调用 stage-bundle.sh 汇总随包载荷。
已存在的压缩包会复用（缓存在 artifacts/cache/models/downloads）。

选项:
  --with-rapidocr   透传给 stage-bundle.sh：额外打包 RapidOCR 模型
  --with-whisper    透传给 stage-bundle.sh：额外打包 Whisper ggml-small
  --dry-run         只列出将下载的资产，不联网、不写入
  -h, --help        显示本帮助

退出码: 0 成功 / 1 下载或资产校验失败 / 2 用法错误
EOF
}

log() { printf '[fetch-bundle] %s\n' "$*"; }
die() { printf '[fetch-bundle] 错误：%s\n' "$*" >&2; exit 1; }

PASSTHROUGH=()
DRY_RUN=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --with-rapidocr|--with-whisper) PASSTHROUGH+=("$1"); shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
  esac
done

ARTIFACTS_ROOT="${AIASSISTANT_ARTIFACTS_ROOT:-$REPO_ROOT/artifacts}"
MODELS="$ARTIFACTS_ROOT/cache/models"
CACHE="$MODELS/downloads"
REPO="k2-fsa/sherpa-onnx"

if [[ "$DRY_RUN" -eq 1 ]]; then
  log "dry-run：缓存目录 $CACHE"
  for spec in \
    "kws-models sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01.tar.bz2" \
    "asr-models sherpa-onnx-paraformer-zh-small-2024-03-09.tar.bz2" \
    "tts-models vits-melo-tts-zh_en.tar.bz2" \
    "asr-models silero_vad_v5.onnx"; do
    tag="${spec%% *}"; name="${spec#* }"
    if [[ -f "$CACHE/$name" ]]; then
      log "  已缓存  $name"
    else
      log "  待下载  $name  ($tag)"
    fi
  done
  log "dry-run 结束，未联网、未写入。"
  exit 0
fi

mkdir -p "$CACHE"

fetch() {
  local tag="$1" pattern="$2"
  local target="$CACHE/$pattern"
  if [[ -f "$target" ]]; then
    log "cached: $pattern"
    return
  fi

  log "fetching: $pattern"
  if command -v gh >/dev/null 2>&1 && gh auth status >/dev/null 2>&1; then
    gh release download "$tag" --repo "$REPO" --pattern "$pattern" --dir "$CACHE" --clobber
  else
    curl -fL --retry 3 --retry-delay 2 \
      "https://github.com/$REPO/releases/download/$tag/$pattern" -o "$target"
  fi
}

extract() {
  local archive="$1" dest="$2"
  # GNU tar (含 Git Bash) 会把 "C:/..." 误判为远程主机，--force-local 强制按本地路径处理。
  local tar_opts=()
  if tar --version 2>/dev/null | grep -q 'GNU tar'; then
    tar_opts+=(--force-local)
  fi
  mkdir -p "$dest"
  tar -xjf "$archive" -C "$dest" ${tar_opts[@]+"${tar_opts[@]}"}
}

fetch kws-models sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01.tar.bz2
fetch asr-models sherpa-onnx-paraformer-zh-small-2024-03-09.tar.bz2
fetch tts-models vits-melo-tts-zh_en.tar.bz2
fetch asr-models silero_vad_v5.onnx

KWS_ROOT="$CACHE/kws-extract"
ASR_ROOT="$CACHE/asr-extract"
TTS_ROOT="$CACHE/tts-extract"
rm -rf "$KWS_ROOT" "$ASR_ROOT" "$TTS_ROOT"
mkdir -p "$KWS_ROOT" "$ASR_ROOT" "$TTS_ROOT"

extract "$CACHE/sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01.tar.bz2" "$KWS_ROOT"
extract "$CACHE/sherpa-onnx-paraformer-zh-small-2024-03-09.tar.bz2" "$ASR_ROOT"
extract "$CACHE/vits-melo-tts-zh_en.tar.bz2" "$TTS_ROOT"

KWS_SRC="$(find "$KWS_ROOT" -type d -name 'sherpa-onnx-kws-*' | head -1)"
ASR_SRC="$(find "$ASR_ROOT" -type d -name 'sherpa-onnx-paraformer-*' | head -1)"
TTS_MODEL="$(find "$TTS_ROOT" -type f -name 'model.onnx' | head -1)"
if [[ -z "$TTS_MODEL" ]]; then
  die "TTS 压缩包内没有 model.onnx：$CACHE/vits-melo-tts-zh_en.tar.bz2"
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

rm -rf "$MODELS/sherpa-tts"
mkdir -p "$MODELS/sherpa-tts/dict"
cp "$TTS_MODEL" "$MODELS/sherpa-tts/model.onnx"
cp "$TTS_DIR/tokens.txt" "$TTS_DIR/lexicon.txt" "$MODELS/sherpa-tts/"
cp "$TTS_DIR"/*.fst "$MODELS/sherpa-tts/"
cp -R "$TTS_DIR/dict/." "$MODELS/sherpa-tts/dict/"
if [[ -f "$TTS_DIR/LICENSE" ]]; then
  cp "$TTS_DIR/LICENSE" "$MODELS/sherpa-tts/LICENSE"
fi
cp "$CACHE/silero_vad_v5.onnx" "$MODELS/sherpa-vad/silero_vad.onnx"

log "资产已展开到 $MODELS，开始汇总随包载荷。"
bash "$SCRIPT_DIR/stage-bundle.sh" ${PASSTHROUGH+"${PASSTHROUGH[@]}"}
