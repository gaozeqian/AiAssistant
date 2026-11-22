#!/usr/bin/env bash
# 安装当前用户的 .desktop 入口与 hicolor 图标，让 GNOME 顶栏与 Dock
# 显示应用身份而不是通用占位图标。
#
# 匹配键：
#   - Wayland: ShellApp 的 app_id = "aiassistant"（必须与 <file>.desktop 同名）
#   - X11:     StartupWMClass = AiAssistant.Host（Avalonia 为该二进制设置的 WM_CLASS）
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null || true)"
if [[ -z "$REPO_ROOT" ]]; then
  echo "错误：无法定位仓库根目录（$SCRIPT_DIR 不在 Git 工作区内）。" >&2
  exit 1
fi

usage() {
  cat <<'EOF'
用法: bash tools/install-desktop-entry.sh [可执行文件路径] [选项]

  [可执行文件路径]  默认 src/AiAssistant.Host/bin/Release/net10.0/AiAssistant.Host

选项:
  --dry-run         只打印将写入的路径与内容，不落盘
  -h, --help        显示本帮助

退出码: 0 成功 / 1 二进制缺失 / 2 用法错误
EOF
}

BINARY=""
DRY_RUN=0
for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    -h|--help) usage; exit 0 ;;
    -*) usage >&2; exit 2 ;;
    *)
      if [[ -n "$BINARY" ]]; then usage >&2; exit 2; fi
      BINARY="$arg"
      ;;
  esac
done

BINARY="${BINARY:-$REPO_ROOT/src/AiAssistant.Host/bin/Release/net10.0/AiAssistant.Host}"

if [[ ! -x "$BINARY" ]]; then
  echo "binary not executable: $BINARY" >&2
  echo "build first: dotnet build src/AiAssistant.Host/AiAssistant.Host.csproj -c Release" >&2
  exit 1
fi

DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
APPS="$DATA_HOME/applications"
ICONS="$DATA_HOME/icons/hicolor/256x256/apps"

if [[ "$DRY_RUN" -eq 1 ]]; then
  echo "dry-run：将写入"
  echo "  $APPS/aiassistant.desktop"
  echo "  $ICONS/aiassistant.png"
  echo "  Exec=$BINARY --show-window"
  echo "dry-run 结束，未写入任何文件。"
  exit 0
fi

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
