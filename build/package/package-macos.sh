#!/usr/bin/env bash
# 构建 macOS .app bundle，签名（Developer ID + hardened runtime）、可选公证，并产出可分发的 zip / pkg。
# 签名与公证必须在 macOS 上执行；布局装配可在任意 POSIX shell 用 --skip-sign 完成（CI 冒烟用）。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null || true)"
if [[ -z "$REPO_ROOT" ]]; then
  echo "错误：无法定位仓库根目录（$SCRIPT_DIR 不在 Git 工作区内）。" >&2
  exit 1
fi

usage() {
  cat <<'EOF'
用法: bash build/package/package-macos.sh [选项]

产出 AiAssistant.app、AiAssistant-<version>-osx-arm64.zip 与（在 macOS 上）.pkg。

选项:
  --version <版本>          指定版本，默认取 Directory.Build.props 的 VersionPrefix
  --identity <标识>         codesign 标识，默认读 $AIASSISTANT_CODESIGN_IDENTITY
  --notary-profile <名称>   notarytool 钥匙串配置名，默认读 $AIASSISTANT_NOTARY_PROFILE
  --output <目录>           产物目录，默认 $AIASSISTANT_PACKAGE_OUTPUT 或 artifacts/packages
  --runtime <RID>           目标运行时，默认 osx-arm64
  --certificate <路径>      自签名证书，随 pkg 一起分发并在 postinstall 导入
  --keychain <路径>         签名使用的钥匙串
  --require-sign            强制要求签名（缺标识或与 --skip-sign 同用即失败）
  --skip-sign               跳过 codesign / 公证（仅布局装配）
  --skip-models             不随包语音模型（仅调试，正式发布禁用）
  --framework-dependent     产出依赖框架的包（默认自包含）
  -h, --help                显示本帮助

退出码: 0 成功 / 1 构建、签名或资产缺失 / 2 用法错误
EOF
}

require_value() {
  [[ $# -ge 2 && -n "$2" ]] || { usage >&2; exit 2; }
}

VERSION="${VERSION:-}"
IDENTITY="${AIASSISTANT_CODESIGN_IDENTITY:-}"
NOTARY_PROFILE="${AIASSISTANT_NOTARY_PROFILE:-}"
OUTPUT_ROOT="${AIASSISTANT_PACKAGE_OUTPUT:-artifacts/packages}"
SKIP_SIGN=0
SKIP_MODELS=0
REQUIRE_SIGN=0
CERTIFICATE_PATH=""
FRAMEWORK_DEPENDENT=0
RUNTIME="osx-arm64"
KEYCHAIN=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) require_value "$@"; VERSION="$2"; shift 2 ;;
    --identity) require_value "$@"; IDENTITY="$2"; shift 2 ;;
    --notary-profile) require_value "$@"; NOTARY_PROFILE="$2"; shift 2 ;;
    --output) require_value "$@"; OUTPUT_ROOT="$2"; shift 2 ;;
    --runtime) require_value "$@"; RUNTIME="$2"; shift 2 ;;
    --skip-sign) SKIP_SIGN=1; shift ;;
    --skip-models) SKIP_MODELS=1; shift ;;
    --require-sign) REQUIRE_SIGN=1; shift ;;
    --certificate) require_value "$@"; CERTIFICATE_PATH="$2"; shift 2 ;;
    --keychain) require_value "$@"; KEYCHAIN="$2"; shift 2 ;;
    --framework-dependent) FRAMEWORK_DEPENDENT=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

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

if [[ "$REQUIRE_SIGN" -eq 1 ]]; then
  if [[ "$SKIP_SIGN" -eq 1 ]]; then
    echo "error: --require-sign cannot be combined with --skip-sign" >&2
    exit 1
  fi
  if [[ -z "$IDENTITY" ]]; then
    echo "error: --require-sign requires AIASSISTANT_CODESIGN_IDENTITY" >&2
    exit 1
  fi
  if [[ -z "$NOTARY_PROFILE" ]]; then
    echo "warning: no notary profile supplied; self-signed build will not be notarized" >&2
  fi
fi

STAGE="$OUT_DIR/staging/$RUNTIME"
APP="$STAGE/AiAssistant.app"
CONTENTS="$APP/Contents"
SELF_CONTAINED="true"
if [[ "$FRAMEWORK_DEPENDENT" -eq 1 ]]; then
  SELF_CONTAINED="false"
fi

echo "== AiAssistant macOS package =="
echo "version=$VERSION runtime=$RUNTIME selfContained=$SELF_CONTAINED identity=${IDENTITY:-none}"

rm -rf "$STAGE"
mkdir -p "$CONTENTS/MacOS" "$CONTENTS/Resources"

dotnet publish "$REPO_ROOT/src/AiAssistant.Host/AiAssistant.Host.csproj" \
  -c Release -r "$RUNTIME" --self-contained "$SELF_CONTAINED" \
  -p:Version="$VERSION" -p:InformationalVersion="$VERSION" \
  -tl:off -v:m \
  -o "$CONTENTS/MacOS"

HELPER="$REPO_ROOT/native/AiAssistant.MacHelper/.build/release/aiassistant-mac-helper"
if [[ -x "$HELPER" ]]; then
  cp "$HELPER" "$CONTENTS/MacOS/aiassistant-mac-helper"
  chmod +x "$CONTENTS/MacOS/aiassistant-mac-helper"
else
  echo "warning: Swift helper binary not found; build native/AiAssistant.MacHelper first" >&2
fi

# Local model assets ship inside the .app (Contents/MacOS/models, beside the executable).
# Only the chat/embedding models stay external.
if [[ "$SKIP_MODELS" -eq 1 ]]; then
  echo "warning: --skip-models set; the app will not include voice model assets" >&2
else
  MODEL_BUNDLE="$REPO_ROOT/artifacts/models/bundle"
  if [[ ! -d "$MODEL_BUNDLE" ]]; then
    echo "model bundle not found: $MODEL_BUNDLE (run build/models/stage-bundle.sh, or pass --skip-models)" >&2
    exit 1
  fi

  cp -R "$MODEL_BUNDLE" "$CONTENTS/MacOS/models"
  for required in "sherpa/kws/encoder.onnx" "sherpa/model.int8.onnx" "sherpa/silero_vad.onnx" "sherpa/tts/model.onnx"; do
    if [[ ! -f "$CONTENTS/MacOS/models/$required" ]]; then
      echo "bundled model missing: $required" >&2
      exit 1
    fi
  done
  echo "bundled models: $(du -sh "$CONTENTS/MacOS/models" | cut -f1)"
fi

bash "$SCRIPT_DIR/legal-files.sh" "$CONTENTS/Resources"

sed "s/__VERSION__/$VERSION/g" "$SCRIPT_DIR/macos/Info.plist.template" > "$CONTENTS/Info.plist"
chmod +x "$CONTENTS/MacOS/AiAssistant.Host"

# Build a .icns when Apple tooling is available; otherwise ship the PNG for reference.
if command -v iconutil >/dev/null 2>&1 && command -v sips >/dev/null 2>&1; then
  ICONSET="$STAGE/AiAssistant.iconset"
  mkdir -p "$ICONSET"
  for size in 16 32 64 128 256 512; do
    sips -z "$size" "$size" "$SCRIPT_DIR/assets/aiassistant.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$SCRIPT_DIR/assets/aiassistant.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
  done
  iconutil -c icns "$ICONSET" -o "$CONTENTS/Resources/AiAssistant.icns"
  rm -rf "$ICONSET"
else
  cp "$SCRIPT_DIR/assets/aiassistant.png" "$CONTENTS/Resources/AiAssistant.png"
fi

if [[ "$SKIP_SIGN" -eq 0 ]]; then
  if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "error: codesign requires macOS; re-run with --skip-sign for layout-only assembly" >&2
    exit 1
  fi

  SIGN_IDENTITY="${IDENTITY:--}"
  echo "signing with identity: $SIGN_IDENTITY keychain=${KEYCHAIN:-default}"
  codesign --force --deep --options runtime \
    --entitlements "$SCRIPT_DIR/macos/AiAssistant.entitlements" \
    ${KEYCHAIN:+--keychain "$KEYCHAIN"} \
    --sign "$SIGN_IDENTITY" "$APP"
  codesign --verify --deep --strict --verbose=2 "$APP"

  if [[ -n "$NOTARY_PROFILE" ]]; then
    echo "notarizing with keychain profile: $NOTARY_PROFILE"
    ditto -c -k --keepParent "$APP" "$STAGE/notarize.zip"
    xcrun notarytool submit "$STAGE/notarize.zip" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$APP"
    rm -f "$STAGE/notarize.zip"
  else
    echo "warning: no notary profile supplied; skipping notarization" >&2
  fi
else
  echo "skipping codesign/notarization (--skip-sign)"
fi

mkdir -p "$OUT_DIR"
ZIP_PATH="$OUT_DIR/AiAssistant-$VERSION-$RUNTIME.zip"
rm -f "$ZIP_PATH"
if command -v ditto >/dev/null 2>&1; then
  ditto -c -k --keepParent "$APP" "$ZIP_PATH"
elif command -v zip >/dev/null 2>&1; then
  (cd "$STAGE" && zip -qry "$ZIP_PATH" "AiAssistant.app")
else
  echo "warning: neither ditto nor zip is available; bundle left at $APP" >&2
fi

echo "app bundle: $APP"
if [[ -f "$ZIP_PATH" ]]; then
  echo "zip: $ZIP_PATH"
fi

PKG_PATH="$OUT_DIR/AiAssistant-$VERSION-$RUNTIME.pkg"
if [[ "$(uname -s)" == "Darwin" ]]; then
  PKG_ROOT="$STAGE/pkgroot"
  PKG_SCRIPTS="$STAGE/pkgscripts"
  rm -rf "$PKG_ROOT" "$PKG_SCRIPTS"
  mkdir -p "$PKG_ROOT/Applications" "$PKG_SCRIPTS"

  if [[ -n "$CERTIFICATE_PATH" && -f "$CERTIFICATE_PATH" ]]; then
    mkdir -p "$PKG_ROOT/usr/local/share/aiassistant"
    cp "$CERTIFICATE_PATH" "$PKG_ROOT/usr/local/share/aiassistant/AiAssistant.cer"
    cat > "$PKG_SCRIPTS/postinstall" <<'POSTINSTALL'
#!/bin/bash
set -e
CERT="/usr/local/share/aiassistant/AiAssistant.cer"
if [ -f "$CERT" ]; then
  /usr/bin/security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain "$CERT" || true
fi
exit 0
POSTINSTALL
    chmod +x "$PKG_SCRIPTS/postinstall"
  fi

  cp -R "$APP" "$PKG_ROOT/Applications/"
  rm -f "$PKG_PATH"
  pkgbuild --root "$PKG_ROOT" --scripts "$PKG_SCRIPTS" \
    --identifier "com.aiassistant.app" --version "$VERSION" \
    --install-location "/" "$PKG_PATH" >/dev/null
  echo "pkg: $PKG_PATH"
else
  echo "warning: pkgbuild is macOS-only; skipping .pkg" >&2
fi

write_checksums() {
  local directory="$1"
  (
    cd "$directory" || return 1
    : > SHA256SUMS
    for file in *.zip *.tar.gz *.deb *.dmg *.pkg; do
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
echo "macOS package complete."
