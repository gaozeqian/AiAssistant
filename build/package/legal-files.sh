#!/usr/bin/env bash
# 把产品法务文件与第三方声明复制到打包目录。
# 任一文件缺失即失败，保证发布物不会缺法务文件。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null || true)"
if [[ -z "$REPO_ROOT" ]]; then
  echo "错误：无法定位仓库根目录（$SCRIPT_DIR 不在 Git 工作区内）。" >&2
  exit 1
fi

usage() {
  cat <<'EOF'
用法: bash build/package/legal-files.sh <目标目录> [选项]

复制四个法务文件到目标目录：
  LICENSE、EULA.md、THIRD-PARTY-NOTICES.md、THIRD-PARTY-NOTICES-ONNXRUNTIME.txt
最后一份从 NuGet 缓存里的 Microsoft.ML.OnnxRuntime.Managed 包提取，需要先 dotnet restore。

选项:
  -h, --help   显示本帮助

退出码: 0 成功 / 1 法务文件缺失 / 2 用法错误
EOF
}

DEST=""
for arg in "$@"; do
  case "$arg" in
    -h|--help) usage; exit 0 ;;
    -*) usage >&2; exit 2 ;;
    *)
      if [[ -n "$DEST" ]]; then usage >&2; exit 2; fi
      DEST="$arg"
      ;;
  esac
done

if [[ -z "$DEST" ]]; then
  usage >&2
  exit 2
fi

mkdir -p "$DEST"

for name in LICENSE EULA.md THIRD-PARTY-NOTICES.md; do
  if [[ ! -f "$REPO_ROOT/$name" ]]; then
    echo "missing legal file: $REPO_ROOT/$name" >&2
    exit 1
  fi
  cp "$REPO_ROOT/$name" "$DEST/$name"
done

NUGET_ROOT="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
ORT_VERSION="$(sed -n 's:.*Include="Microsoft.ML.OnnxRuntime.Managed" Version="\([^"]*\)".*:\1:p' "$REPO_ROOT/Directory.Packages.props" | head -1)"
if [[ -z "$ORT_VERSION" ]]; then
  echo "unable to read Microsoft.ML.OnnxRuntime.Managed version from Directory.Packages.props" >&2
  exit 1
fi

ORT_NOTICES="$NUGET_ROOT/microsoft.ml.onnxruntime.managed/$ORT_VERSION/ThirdPartyNotices.txt"
if [[ ! -f "$ORT_NOTICES" ]]; then
  echo "missing ONNX Runtime notices: $ORT_NOTICES (run 'dotnet restore' first)" >&2
  exit 1
fi
cp "$ORT_NOTICES" "$DEST/THIRD-PARTY-NOTICES-ONNXRUNTIME.txt"

echo "legal files -> $DEST (LICENSE, EULA.md, THIRD-PARTY-NOTICES.md, THIRD-PARTY-NOTICES-ONNXRUNTIME.txt)"
