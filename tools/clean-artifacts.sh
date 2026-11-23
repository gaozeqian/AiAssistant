#!/usr/bin/env bash
# 按保留策略清理 artifacts/。默认 dry-run，只有显式 --apply 才删除。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null || true)"
if [[ -z "$REPO_ROOT" ]]; then
  echo "错误：无法定位仓库根目录（$SCRIPT_DIR 不在 Git 工作区内）。" >&2
  exit 1
fi

usage() {
  cat <<'EOF'
用法: bash tools/clean-artifacts.sh [选项]

按生命周期分层清理产物目录。默认 dry-run，不删除任何文件。

保留策略:
  bin/ obj/ publish/ staging/ test/ tmp/ build/ evidence/  可重建或旧布局，默认全部可删
  dist/<version>/                    最终分发目录，保留最新 1 个版本
  cache/models/                      外部模型缓存；仅在 --models 时清理，_retired 永不清理
  test-results/e2e/                  UI 截图与报告，保留最近 5 轮
  test-results/coverage/             覆盖率报告，仅在 --coverage 时清理
  test-results/benchmarks/ soak/ golden/ retired-models/  永久保留

选项:
  --apply            真正删除（默认只打印计划）
  --dry-run          显式声明只打印计划（默认行为）
  --models           同时清理 cache/models（不含 _retired）
  --coverage         同时清理 test-results/coverage
  --yes              跳过逐目录确认（仅用于非交互环境）
  -h, --help         显示本帮助

退出码: 0 成功 / 1 清理失败 / 2 用法错误
EOF
}

log() { printf '[clean-artifacts] %s\n' "$*"; }
die() { printf '[clean-artifacts] 错误：%s\n' "$*" >&2; exit 1; }

APPLY=0
WITH_MODELS=0
WITH_COVERAGE=0
ASSUME_YES=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --apply) APPLY=1; shift ;;
    --dry-run) APPLY=0; shift ;;
    --models) WITH_MODELS=1; shift ;;
    --coverage) WITH_COVERAGE=1; shift ;;
    --yes) ASSUME_YES=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
  esac
done

ROOT="${AIASSISTANT_ARTIFACTS_ROOT:-$REPO_ROOT/artifacts}"
if [[ ! -d "$ROOT" ]]; then
  log "产物目录不存在，无需清理：$ROOT"
  exit 0
fi

log "产物根：$ROOT"
[[ "$APPLY" -eq 1 ]] || log "模式：dry-run（加 --apply 才会删除）"

PLAN=()
add_path() { PLAN+=("$1|$2|$3"); }

for dir in bin obj publish staging test tmp build evidence; do
  [[ -d "$ROOT/$dir" ]] && add_path "可重建" "$ROOT/$dir" "构建/暂存/临时或旧布局"
done

# dist：保留版本号最大的一个目录，删除其余。
if [[ -d "$ROOT/dist" ]]; then
  versions="$(
    find "$ROOT/dist" -mindepth 1 -maxdepth 1 -type d 2>/dev/null |
      while IFS= read -r d; do basename "$d"; done |
      sed -n 's/^\([0-9][0-9.]*\)$/\1/p' | sort -u
  )"
  if [[ -n "$versions" ]]; then
    ordered="$(
      printf '%s\n' "$versions" | while IFS= read -r v; do
        key="$(printf '%s\n' "$v" | awk -F. '{ printf "%06d%06d%06d", $1+0, $2+0, $3+0 }')"
        printf '%s\t%s\n' "$key" "$v"
      done | sort -k1,1n | cut -f2
    )"
    old_versions="$(printf '%s\n' "$ordered" | sed '$d')"
    while IFS= read -r v; do
      [[ -n "$v" ]] || continue
      add_path "旧版本分发" "$ROOT/dist/$v" "保留最新版本"
    done <<<"$old_versions"
  fi
fi

# test-results/e2e：保留最近 5 轮（按目录名倒序）。
if [[ -d "$ROOT/test-results/e2e" ]]; then
  while IFS= read -r old; do
    [[ -n "$old" ]] || continue
    add_path "旧 E2E 轮次" "$ROOT/test-results/e2e/$old" "保留最近 5 轮"
  done < <(
    find "$ROOT/test-results/e2e" -mindepth 1 -maxdepth 1 -type d |
      while IFS= read -r d; do basename "$d"; done |
      sort -r | tail -n +6
  )
fi

if [[ "$WITH_COVERAGE" -eq 1 && -d "$ROOT/test-results/coverage" ]]; then
  add_path "覆盖率报告" "$ROOT/test-results/coverage" "可重新生成"
fi

if [[ "$WITH_MODELS" -eq 1 && -d "$ROOT/cache/models" ]]; then
  while IFS= read -r entry; do
    [[ -n "$entry" ]] || continue
    case "$(basename "$entry")" in
      _retired) continue ;;
    esac
    add_path "模型缓存" "$entry" "可重建（保留 _retired）"
  done < <(find "$ROOT/cache/models" -mindepth 1 -maxdepth 1)
fi

if [[ ${#PLAN[@]} -eq 0 ]]; then
  log "没有符合清理条件的条目。"
  exit 0
fi

log "计划删除 ${#PLAN[@]} 项："
for item in "${PLAN[@]}"; do
  IFS='|' read -r label path reason <<<"$item"
  size="$(du -sh "$path" 2>/dev/null | cut -f1 || echo '?')"
  printf '  - [%s] %s (%s, %s)\n' "$label" "${path#"$ROOT/"}" "$size" "$reason"
done

if [[ "$APPLY" -eq 0 ]]; then
  log "dry-run 结束，未删除任何文件。确认后加 --apply 执行。"
  exit 0
fi

if [[ "$ASSUME_YES" -eq 0 && ! -t 0 ]]; then
  die "非交互环境必须显式加 --yes 才能删除。"
fi

for item in "${PLAN[@]}"; do
  IFS='|' read -r label path reason <<<"$item"
  if [[ "$ASSUME_YES" -eq 0 ]]; then
    printf '[clean-artifacts] 删除 %s（%s）？输入 yes 确认：' "${path#"$ROOT/"}" "$label"
    read -r answer
    [[ "$answer" == "yes" ]] || { log "跳过 ${path#"$ROOT/"}"; continue; }
  fi
  rm -rf -- "$path"
  log "已删除 ${path#"$ROOT/"}"
done

log "清理完成。"
