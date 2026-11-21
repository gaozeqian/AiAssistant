#!/usr/bin/env bash
# Builds the macOS .app bundle, signs it (Developer ID + hardened runtime), optionally notarizes it,
# and produces a distributable zip. Must run on macOS for signing/notarization; the layout can be
# assembled on any POSIX shell with --skip-sign for CI smoke checks.
set -euo pipefail

VERSION="${VERSION:-}"
IDENTITY="${AIASSISTANT_CODESIGN_IDENTITY:-}"
NOTARY_PROFILE="${AIASSISTANT_NOTARY_PROFILE:-}"
OUTPUT_ROOT="${AIASSISTANT_PACKAGE_OUTPUT:-artifacts/packages}"
SKIP_SIGN=0
REQUIRE_SIGN=0
CERTIFICATE_PATH=""
FRAMEWORK_DEPENDENT=0
RUNTIME="osx-arm64"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="$2"; shift 2 ;;
    --identity) IDENTITY="$2"; shift 2 ;;
    --notary-profile) NOTARY_PROFILE="$2"; shift 2 ;;
    --output) OUTPUT_ROOT="$2"; shift 2 ;;
    --runtime) RUNTIME="$2"; shift 2 ;;
    --skip-sign) SKIP_SIGN=1; shift ;;
    --require-sign) REQUIRE_SIGN=1; shift ;;
    --certificate) CERTIFICATE_PATH="$2"; shift 2 ;;
    --framework-dependent) FRAMEWORK_DEPENDENT=1; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
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

STAGE="$REPO_ROOT/$OUTPUT_ROOT/staging/$RUNTIME"
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
  echo "signing with identity: $SIGN_IDENTITY"
  codesign --force --deep --options runtime \
    --entitlements "$SCRIPT_DIR/macos/AiAssistant.entitlements" \
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

mkdir -p "$REPO_ROOT/$OUTPUT_ROOT"
ZIP_PATH="$REPO_ROOT/$OUTPUT_ROOT/AiAssistant-$VERSION-$RUNTIME.zip"
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

PKG_PATH="$REPO_ROOT/$OUTPUT_ROOT/AiAssistant-$VERSION-$RUNTIME.pkg"
if [[ "$(uname -s)" == "Darwin" && -n "$CERTIFICATE_PATH" && -f "$CERTIFICATE_PATH" ]]; then
  PKG_ROOT="$STAGE/pkgroot"
  PKG_SCRIPTS="$STAGE/pkgscripts"
  rm -rf "$PKG_ROOT" "$PKG_SCRIPTS"
  mkdir -p "$PKG_ROOT/Applications" "$PKG_ROOT/usr/local/share/aiassistant" "$PKG_SCRIPTS"
  cp -R "$APP" "$PKG_ROOT/Applications/"
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
  rm -f "$PKG_PATH"
  pkgbuild --root "$PKG_ROOT" --scripts "$PKG_SCRIPTS"     --identifier "com.aiassistant.app" --version "$VERSION"     --install-location "/" "$PKG_PATH" >/dev/null
  echo "pkg: $PKG_PATH"
else
  echo "warning: pkgbuild requires macOS and --certificate; skipping .pkg" >&2
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

write_checksums "$REPO_ROOT/$OUTPUT_ROOT"
echo "macOS package complete."
