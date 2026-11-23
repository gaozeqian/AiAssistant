#!/usr/bin/env bash
# 构建 Linux x64 发布产物：便携 tar.gz，若存在 dpkg-deb（或 python 兜底）则额外产出 .deb。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null || true)"
if [[ -z "$REPO_ROOT" ]]; then
  echo "错误：无法定位仓库根目录（$SCRIPT_DIR 不在 Git 工作区内）。" >&2
  exit 1
fi

usage() {
  cat <<'EOF'
用法: bash build/package/package-linux.sh [选项]

产出 AiAssistant-<version>-linux-x64.tar.gz 与（可用时）.deb。

选项:
  --version <版本>        指定版本，默认取 Directory.Build.props 的 VersionPrefix
  --output <目录>         产物目录，默认 $AIASSISTANT_PACKAGE_OUTPUT 或 artifacts/package
  --runtime <RID>         目标运行时，默认 linux-x64
  --framework-dependent   产出依赖框架的包（默认自包含）
  --skip-models           不随包语音模型（仅调试，正式发布禁用）
  -h, --help              显示本帮助

退出码: 0 成功 / 1 构建或资产缺失 / 2 用法错误
EOF
}

require_value() {
  [[ $# -ge 2 && -n "$2" ]] || { usage >&2; exit 2; }
}

VERSION="${VERSION:-}"
OUTPUT_ROOT="${AIASSISTANT_PACKAGE_OUTPUT:-artifacts/package}"
RUNTIME="linux-x64"
FRAMEWORK_DEPENDENT=0
SKIP_MODELS=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) require_value "$@"; VERSION="$2"; shift 2 ;;
    --output) require_value "$@"; OUTPUT_ROOT="$2"; shift 2 ;;
    --runtime) require_value "$@"; RUNTIME="$2"; shift 2 ;;
    --framework-dependent) FRAMEWORK_DEPENDENT=1; shift ;;
    --skip-models) SKIP_MODELS=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

ARTIFACTS_ROOT="${AIASSISTANT_ARTIFACTS_ROOT:-$REPO_ROOT/artifacts}"
if [[ "$OUTPUT_ROOT" = /* ]]; then
  OUT_DIR="$OUTPUT_ROOT"
else
  OUT_DIR="$REPO_ROOT/$OUTPUT_ROOT"
fi

if [[ -z "$VERSION" ]]; then
  VERSION="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$REPO_ROOT/Directory.Build.props" | head -1)"
fi
VERSION="${VERSION#v}"
if [[ -z "$VERSION" ]]; then
  echo "unable to determine version; pass --version" >&2
  exit 1
fi

SELF_CONTAINED="true"
if [[ "$FRAMEWORK_DEPENDENT" -eq 1 ]]; then
  SELF_CONTAINED="false"
fi

STAGE="$ARTIFACTS_ROOT/build/staging/$RUNTIME"
APP_DIR="$STAGE/usr/lib/aiassistant"

echo "== AiAssistant Linux package =="
echo "version=$VERSION runtime=$RUNTIME selfContained=$SELF_CONTAINED"

rm -rf "$STAGE"
mkdir -p "$APP_DIR" \
  "$STAGE/usr/bin" \
  "$STAGE/usr/share/applications" \
  "$STAGE/usr/share/icons/hicolor/256x256/apps" \
  "$STAGE/DEBIAN"

dotnet publish "$REPO_ROOT/src/AiAssistant.Host/AiAssistant.Host.csproj" \
  -c Release -r "$RUNTIME" --self-contained "$SELF_CONTAINED" \
  -p:Version="$VERSION" -p:InformationalVersion="$VERSION" \
  -tl:off -v:m \
  -o "$APP_DIR"

# Local model assets ship beside the executable (/usr/lib/aiassistant/models).
# Only the chat/embedding models stay external.
if [[ "$SKIP_MODELS" -eq 1 ]]; then
  echo "warning: --skip-models set; the package will not include voice model assets" >&2
else
  MODEL_BUNDLE="$ARTIFACTS_ROOT/cache/models/bundle"
  if [[ ! -d "$MODEL_BUNDLE" ]]; then
    echo "model bundle not found: $MODEL_BUNDLE (run build/models/stage-bundle.sh, or pass --skip-models)" >&2
    exit 1
  fi

  cp -R "$MODEL_BUNDLE" "$APP_DIR/models"
  for required in "sherpa/kws/encoder.onnx" "sherpa/model.int8.onnx" "sherpa/silero_vad.onnx" "sherpa/tts/model.onnx"; do
    if [[ ! -f "$APP_DIR/models/$required" ]]; then
      echo "bundled model missing: $required" >&2
      exit 1
    fi
  done
  echo "bundled models: $(du -sh "$APP_DIR/models" | cut -f1)"
fi

bash "$SCRIPT_DIR/legal-files.sh" "$APP_DIR"
mkdir -p "$STAGE/usr/share/doc/aiassistant"
cp "$APP_DIR/LICENSE" "$APP_DIR/EULA.md" "$APP_DIR/THIRD-PARTY-NOTICES.md" \
   "$APP_DIR/THIRD-PARTY-NOTICES-ONNXRUNTIME.txt" "$STAGE/usr/share/doc/aiassistant/"

cat > "$STAGE/usr/bin/aiassistant" <<'LAUNCHER'
#!/bin/sh
exec /usr/lib/aiassistant/AiAssistant.Host "$@"
LAUNCHER
chmod +x "$STAGE/usr/bin/aiassistant"
chmod +x "$APP_DIR/AiAssistant.Host"

cp "$SCRIPT_DIR/linux/aiassistant.desktop" "$STAGE/usr/share/applications/aiassistant.desktop"
cp "$SCRIPT_DIR/assets/aiassistant.png" "$STAGE/usr/share/icons/hicolor/256x256/apps/aiassistant.png"
sed "s/__VERSION__/$VERSION/g" "$SCRIPT_DIR/linux/control.template" > "$STAGE/DEBIAN/control"
chmod 0644 "$STAGE/DEBIAN/control"

mkdir -p "$OUT_DIR"
TARBALL="$OUT_DIR/AiAssistant-$VERSION-$RUNTIME.tar.gz"
rm -f "$TARBALL"
tar -czf "$TARBALL" --exclude=DEBIAN -C "$STAGE" .
echo "tar.gz: $TARBALL"

DEB="$OUT_DIR/AiAssistant-$VERSION-$RUNTIME.deb"
rm -f "$DEB"
if command -v dpkg-deb >/dev/null 2>&1; then
  dpkg-deb --build --root-owner-group "$STAGE" "$DEB" >/dev/null
  echo "deb: $DEB"
elif command -v python3 >/dev/null 2>&1; then
  python3 "$SCRIPT_DIR/deb/build-deb.py" "$STAGE" "$DEB"
elif command -v python >/dev/null 2>&1; then
  python "$SCRIPT_DIR/deb/build-deb.py" "$STAGE" "$DEB"
else
  echo "warning: neither dpkg-deb nor python found; skipping .deb" >&2
fi

write_checksums() {
  local directory="$1"
  (
    cd "$directory" || return 1
    : > SHA256SUMS
    for file in *.tar.gz *.deb *.zip; do
      [ -f "$file" ] || continue
      if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$file" >> SHA256SUMS
      else
        shasum -a 256 "$file" >> SHA256SUMS
      fi
    done
    if [ -s SHA256SUMS ]; then
      echo "checksums: $directory/SHA256SUMS"
    else
      rm -f SHA256SUMS
    fi
  )
}

write_checksums "$OUT_DIR"
echo "Linux package complete."
