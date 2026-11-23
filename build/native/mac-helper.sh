#!/usr/bin/env bash
# macOS Swift Helper 的统一构建/测试入口。
# SwiftPM 的 scratch 目录固定到 artifacts/obj/native/mac-helper，源码树不再产生 .build/。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null || true)"
if [[ -z "$REPO_ROOT" ]]; then
  echo "错误：无法定位仓库根目录（$SCRIPT_DIR 不在 Git 工作区内）。" >&2
  exit 1
fi

ARTIFACTS_ROOT="${AIASSISTANT_ARTIFACTS_ROOT:-$REPO_ROOT/artifacts}"
SCRATCH="$ARTIFACTS_ROOT/obj/native/mac-helper"
PACKAGE_DIR="$REPO_ROOT/native/AiAssistant.MacHelper"
HELPER_BINARY="$SCRATCH/release/aiassistant-mac-helper"

usage() {
  cat <<'EOF'
用法: bash build/native/mac-helper.sh <build|test|smoke|path> [swift 参数...]

  build   构建 Swift Helper（release），额外参数原样传给 swift build
  test    运行 MacHelperTests，额外参数原样传给 swift test
  smoke   构建并执行 --smoke-check
  path    打印 release 可执行文件的绝对路径

所有 SwiftPM 中间产物位于 artifacts/obj/native/mac-helper。

退出码: 0 成功 / 1 构建或测试失败 / 2 用法错误或非 macOS 环境
EOF
}

command="${1:-build}"
shift || true

case "$command" in
  -h|--help) usage; exit 0 ;;
  build|test|smoke) ;;
  path) echo "$HELPER_BINARY"; exit 0 ;;
  *) usage >&2; exit 2 ;;
esac

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "错误：macOS Swift Helper 依赖 AppKit/AVFoundation，只能在 Darwin 上执行。" >&2
  exit 2
fi

mkdir -p "$SCRATCH"

case "$command" in
  build)
    swift build --package-path "$PACKAGE_DIR" --scratch-path "$SCRATCH" -c release "$@"
    ;;
  test)
    swift test --package-path "$PACKAGE_DIR" --scratch-path "$SCRATCH" "$@"
    ;;
  smoke)
    swift build --package-path "$PACKAGE_DIR" --scratch-path "$SCRATCH" -c release "$@"
    "$HELPER_BINARY" --smoke-check
    ;;
esac
