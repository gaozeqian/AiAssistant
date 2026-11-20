#!/usr/bin/env bash
# Builds the Linux x64 release artifacts: a portable tar.gz plus a .deb when dpkg-deb is available.
set -euo pipefail

VERSION="${VERSION:-}"
OUTPUT_ROOT="${AIASSISTANT_PACKAGE_OUTPUT:-artifacts/packages}"
RUNTIME="linux-x64"
FRAMEWORK_DEPENDENT=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="$2"; shift 2 ;;
    --output) OUTPUT_ROOT="$2"; shift 2 ;;
    --runtime) RUNTIME="$2"; shift 2 ;;
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

SELF_CONTAINED="true"
if [[ "$FRAMEWORK_DEPENDENT" -eq 1 ]]; then
  SELF_CONTAINED="false"
fi

STAGE="$REPO_ROOT/$OUTPUT_ROOT/staging/$RUNTIME"
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

mkdir -p "$REPO_ROOT/$OUTPUT_ROOT"
TARBALL="$REPO_ROOT/$OUTPUT_ROOT/AiAssistant-$VERSION-$RUNTIME.tar.gz"
rm -f "$TARBALL"
tar -czf "$TARBALL" --exclude=DEBIAN -C "$STAGE" .
echo "tar.gz: $TARBALL"

if command -v dpkg-deb >/dev/null 2>&1; then
  DEB="$REPO_ROOT/$OUTPUT_ROOT/AiAssistant-$VERSION-$RUNTIME.deb"
  rm -f "$DEB"
  dpkg-deb --build --root-owner-group "$STAGE" "$DEB" >/dev/null
  echo "deb: $DEB"
else
  echo "warning: dpkg-deb not found; skipping .deb (tar.gz is still usable)" >&2
fi

echo "Linux package complete."
