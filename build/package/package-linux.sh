#!/usr/bin/env bash
# 构建 Linux x64 分发包：aiassistant-<ver>-linux-x64.tar.gz 与 aiassistant_<ver>_amd64.deb。
# 发布态可执行文件统一命名为 aiassistant；便携包包含顶层目录，避免 Tarbomb。
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

产出:
  artifacts/dist/<version>/aiassistant-<version>-linux-x64.tar.gz
  artifacts/dist/<version>/aiassistant_<version>_amd64.deb

选项:
  --version <版本>        指定版本，默认取 Directory.Build.props 的 VersionPrefix
  --output <目录>         分发目录，默认 artifacts/dist/<version>
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
OUTPUT_ROOT="${AIASSISTANT_PACKAGE_OUTPUT:-}"
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
if [[ -z "$VERSION" ]]; then
  VERSION="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$REPO_ROOT/Directory.Build.props" | head -1)"
fi
VERSION="${VERSION#v}"
if [[ -z "$VERSION" ]]; then
  echo "unable to determine version; pass --version" >&2
  exit 1
fi

if [[ -z "$OUTPUT_ROOT" ]]; then
  DIST_DIR="$ARTIFACTS_ROOT/dist/$VERSION"
elif [[ "$OUTPUT_ROOT" = /* ]]; then
  DIST_DIR="$OUTPUT_ROOT"
else
  DIST_DIR="$REPO_ROOT/$OUTPUT_ROOT"
fi

SELF_CONTAINED="true"
if [[ "$FRAMEWORK_DEPENDENT" -eq 1 ]]; then
  SELF_CONTAINED="false"
fi

PUBLISH_DIR="$ARTIFACTS_ROOT/publish/$RUNTIME"
PORTABLE_NAME="aiassistant-$VERSION-$RUNTIME"
PORTABLE_ROOT="$ARTIFACTS_ROOT/staging/$RUNTIME-portable/$PORTABLE_NAME"
DEB_STAGE="$ARTIFACTS_ROOT/staging/$RUNTIME-deb"
APP_DIR="$DEB_STAGE/usr/lib/aiassistant"

echo "== AiAssistant Linux package =="
echo "version=$VERSION runtime=$RUNTIME selfContained=$SELF_CONTAINED dist=$DIST_DIR"

rm -rf "$PUBLISH_DIR" "$PORTABLE_ROOT" "$DEB_STAGE"
mkdir -p "$PUBLISH_DIR"

dotnet publish "$REPO_ROOT/src/AiAssistant.Host/AiAssistant.Host.csproj" \
  -c Release -r "$RUNTIME" --self-contained "$SELF_CONTAINED" \
  -p:Version="$VERSION" -p:InformationalVersion="$VERSION" \
  -tl:off -v:m \
  -o "$PUBLISH_DIR"

# Local model assets ship beside the executable (/usr/lib/aiassistant/models).
if [[ "$SKIP_MODELS" -eq 1 ]]; then
  echo "warning: --skip-models set; the package will not include voice model assets" >&2
else
  MODEL_BUNDLE="$ARTIFACTS_ROOT/cache/models/bundle"
  if [[ ! -d "$MODEL_BUNDLE" ]]; then
    echo "model bundle not found: $MODEL_BUNDLE (run build/models/stage-bundle.sh, or pass --skip-models)" >&2
    exit 1
  fi

  cp -R "$MODEL_BUNDLE" "$PUBLISH_DIR/models"
  for required in "sherpa/kws/encoder.onnx" "sherpa/model.int8.onnx" "sherpa/silero_vad.onnx" "sherpa/tts/model.onnx"; do
    if [[ ! -f "$PUBLISH_DIR/models/$required" ]]; then
      echo "bundled model missing: $required" >&2
      exit 1
    fi
  done
  echo "bundled models: $(du -sh "$PUBLISH_DIR/models" | cut -f1)"
fi

bash "$SCRIPT_DIR/legal-files.sh" "$PUBLISH_DIR"

# ---- Portable tar.gz with a single top-level directory ----
mkdir -p "$PORTABLE_ROOT"
cp -R "$PUBLISH_DIR/." "$PORTABLE_ROOT/"
if [[ -f "$PORTABLE_ROOT/AiAssistant" ]]; then
  mv "$PORTABLE_ROOT/AiAssistant" "$PORTABLE_ROOT/aiassistant"
fi
chmod +x "$PORTABLE_ROOT/aiassistant" 2>/dev/null || true

mkdir -p "$DIST_DIR"
TARBALL="$DIST_DIR/$PORTABLE_NAME.tar.gz"
rm -f "$TARBALL"
tar -czf "$TARBALL" -C "$(dirname "$PORTABLE_ROOT")" "$PORTABLE_NAME"
echo "tar.gz: $TARBALL"

# ---- Debian package ----
mkdir -p "$APP_DIR" \
  "$DEB_STAGE/usr/bin" \
  "$DEB_STAGE/usr/share/applications" \
  "$DEB_STAGE/usr/share/icons/hicolor/256x256/apps" \
  "$DEB_STAGE/DEBIAN"
cp -R "$PUBLISH_DIR/." "$APP_DIR/"
if [[ -f "$APP_DIR/AiAssistant" ]]; then
  mv "$APP_DIR/AiAssistant" "$APP_DIR/aiassistant"
fi
chmod +x "$APP_DIR/aiassistant" 2>/dev/null || true

mkdir -p "$DEB_STAGE/usr/share/doc/aiassistant"
cp "$APP_DIR/LICENSE" "$APP_DIR/EULA.md" "$APP_DIR/THIRD-PARTY-NOTICES.md" \
   "$APP_DIR/THIRD-PARTY-NOTICES-ONNXRUNTIME.txt" "$DEB_STAGE/usr/share/doc/aiassistant/"

# Relative symlink: no hardcoded /usr/lib path in the launcher.
ln -sf ../lib/aiassistant/aiassistant "$DEB_STAGE/usr/bin/aiassistant"

cp "$SCRIPT_DIR/linux/aiassistant.desktop" "$DEB_STAGE/usr/share/applications/aiassistant.desktop"
cp "$SCRIPT_DIR/assets/aiassistant.png" "$DEB_STAGE/usr/share/icons/hicolor/256x256/apps/aiassistant.png"
sed "s/__VERSION__/$VERSION/g" "$SCRIPT_DIR/linux/control.template" > "$DEB_STAGE/DEBIAN/control"
chmod 0644 "$DEB_STAGE/DEBIAN/control"

DEB="$DIST_DIR/aiassistant_${VERSION}_amd64.deb"
rm -f "$DEB"
if command -v dpkg-deb >/dev/null 2>&1; then
  dpkg-deb --build --root-owner-group "$DEB_STAGE" "$DEB" >/dev/null
  echo "deb: $DEB"
elif command -v python3 >/dev/null 2>&1; then
  PYTHONDONTWRITEBYTECODE=1 python3 "$SCRIPT_DIR/deb/build-deb.py" "$DEB_STAGE" "$DEB"
elif command -v python >/dev/null 2>&1; then
  PYTHONDONTWRITEBYTECODE=1 python "$SCRIPT_DIR/deb/build-deb.py" "$DEB_STAGE" "$DEB"
else
  echo "warning: neither dpkg-deb nor python found; skipping .deb" >&2
fi

bash "$SCRIPT_DIR/checksums.sh" "$DIST_DIR" "$VERSION"
echo "Linux package complete."
