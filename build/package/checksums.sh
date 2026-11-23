#!/usr/bin/env bash
# 为分发包写单包 .sha256 与汇总 AiAssistant-<version>-SHA256SUMS.txt。
set -euo pipefail

usage() {
  cat <<'EOF'
用法: bash build/package/checksums.sh <dist 目录> <版本>

为目录内每个分发包（msi/zip/pkg/deb/tar.gz/cdx.json）生成 <file>.sha256，
并汇总为 AiAssistant-<版本>-SHA256SUMS.txt。清单使用相对文件名。

退出码: 0 成功 / 2 用法错误
EOF
}

[[ $# -eq 2 ]] || { usage >&2; exit 2; }
DIST_DIR="$1"
VERSION="${2#v}"

[[ -d "$DIST_DIR" ]] || { echo "目录不存在：$DIST_DIR" >&2; exit 1; }

digest() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  else
    shasum -a 256 "$1" | awk '{print $1}'
  fi
}

(
  cd "$DIST_DIR"
  SUMMARY="AiAssistant-$VERSION-SHA256SUMS.txt"
  : > "$SUMMARY"
  for file in *.msi *.zip *.pkg *.deb *.tar.gz *.cdx.json; do
    [[ -f "$file" ]] || continue
    hash="$(digest "$file")"
    printf '%s  %s\n' "$hash" "$file" > "$file.sha256"
    printf '%s  %s\n' "$hash" "$file" >> "$SUMMARY"
  done

  if [[ -s "$SUMMARY" ]]; then
    echo "checksums: $DIST_DIR/$SUMMARY"
  else
    rm -f "$SUMMARY"
  fi
)
