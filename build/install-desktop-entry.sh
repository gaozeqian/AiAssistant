#!/usr/bin/env bash
# Installs a per-user .desktop entry + hicolor icon so the GNOME dock and top bar show the app
# identity instead of the generic placeholder.
#
# Match keys:
#   - Wayland: ShellApp sets app_id = "aiassistant" (must equal <file>.desktop)
#   - X11:     StartupWMClass = AiAssistant.Host (Avalonia's WM_CLASS for this binary)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
BINARY="${1:-$REPO_ROOT/src/AiAssistant.Host/bin/Release/net10.0/AiAssistant.Host}"

if [[ ! -x "$BINARY" ]]; then
  echo "binary not executable: $BINARY" >&2
  echo "build first: dotnet build src/AiAssistant.Host/AiAssistant.Host.csproj -c Release" >&2
  exit 1
fi

DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
APPS="$DATA_HOME/applications"
ICONS="$DATA_HOME/icons/hicolor/256x256/apps"
mkdir -p "$APPS" "$ICONS"

cp "$REPO_ROOT/build/package/assets/aiassistant.png" "$ICONS/aiassistant.png"
chmod 0644 "$ICONS/aiassistant.png"

cat > "$APPS/aiassistant.desktop" <<EOF
[Desktop Entry]
Type=Application
Version=1.0
Name=时光
Name[en]=AiAssistant
GenericName=个人活动追踪
Comment=本地加密的活动记录、日报与证据问答
Exec="$BINARY" --show-window
TryExec=$BINARY
Icon=aiassistant
Terminal=false
Categories=Utility;Office;
Keywords=activity;timeline;privacy;ai;
StartupWMClass=AiAssistant.Host
StartupNotify=true
EOF
chmod 0644 "$APPS/aiassistant.desktop"

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$APPS" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -f -t "$DATA_HOME/icons/hicolor" >/dev/null 2>&1 || true
fi

echo "desktop entry: $APPS/aiassistant.desktop"
echo "icon:          $ICONS/aiassistant.png"
