#!/usr/bin/env bash
# 按保留策略清理 artifacts/。默认 dry-run，只有显式 --apply 才删除，且每个子目录单独确认。
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
  build/                 每次构建覆盖          -> 全部可删
  package/               安装包                  -> 保留最新 1 个版本，其余可删
  cache/models/          模型缓存（可重建）      -> 仅在 --models 时清理；_retired 永不清理
  evidence/e2e/          UI 截图与报告          -> 保留最近 5 轮
  evidence/packages/     一次性包布局验证        -> 仅在 --packages 时清理
  evidence/benchmarks/   基准报告                -> 永久保留
  evidence/soak/         浸泡报告                -> 永久保留
  evidence/reports/      运行时导出              -> 永久保留
  evidence/retired-models/ 退役模型证据          -> 永久保留
  tmp/                   测试临时目录            -> 全部可删

选项:
  --apply            真正删除（默认只打印计划）
  --dry-run          显式声明只打印计划（默认行为）
  --models           同时清理 cache/models（不含 _retired）
  --packages         同时清理 evidence/packages
  --yes              跳过逐目录确认（仅用于非交互环境）
  -h, --help         显示本帮助

退出码: 0 成功 / 1 清理失败 / 2 用法错误
EOF
}

log() { printf '[clean-artifacts] %s\n' "$*"; }
die() { printf '[clean-artifacts] 错误：%s\n' "$*" >&2; exit 1; }

APPLY=0
WITH_MODELS=0
WITH_PACKAGES=0
ASSUME_YES=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --apply) APPLY=1; shift ;;
    --dry-run) APPLY=0; shift ;;
    --models) WITH_MODELS=1; shift ;;
    --packages) WITH_PACKAGES=1; shift ;;
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

# 计划：每项为 "标签|绝对路径|说明"
PLAN=()

add_path() { PLAN+=("$1|$2|$3"); }

candidates=()
if [[ -d "$ROOT/build" ]]; then
  candidates+=("$ROOT/build")
fi

if [[ -d "$ROOT/tmp" ]]; then
  candidates+=("$ROOT/tmp")
fi

for path in "${candidates[@]:-}"; do
  [[ -n "${path:-}" ]] || continue
  add_path "构建/临时" "$path" "全部可删"
done

# package/：按文件名里的版本号保留最新一组。
# 仅使用 BSD/GNU 共同支持的 find/sed/sort/tail；不用 find -printf、sort -V、head -n -1。
if [[ -d "$ROOT/package" ]]; then
  versions="$(
    find "$ROOT/package" -maxdepth 1 -type f -name 'AiAssistant-*' 2>/dev/null |
      while IFS= read -r f; do basename "$f"; done |
      sed -n 's/^AiAssistant-\([0-9][0-9.]*\)-.*/\1/p' | sort -u
  )"
  if [[ -n "$versions" ]]; then
    # 将三段版本号补齐为定长数字键，再用普通 sort 排序，最后去掉最新一项。
    ordered="$(
      printf '%s\n' "$versions" | while IFS= read -r v; do
        key="$(printf '%s\n' "$v" | awk -F. '{ printf "%06d%06d%06d", $1+0, $2+0, $3+0 }')"
        printf '%s\t%s\n' "$key" "$v"
      done | sort -k1,1n | cut -f2
    )"
    old_versions="$(printf '%s\n' "$ordered" | sed '$d')"
    if [[ -n "$old_versions" ]]; then
      while IFS= read -r v; do
        [[ -n "$v" ]] || continue
        while IFS= read -r old; do
          [[ -n "$old" ]] || continue
          add_path "旧版本安装包" "$old" "保留最新版本"
        done < <(find "$ROOT/package" -maxdepth 1 -type f -name "AiAssistant-$v-*")
      done <<<"$old_versions"
    fi
  fi
fi

# evidence/e2e/：保留最近 5 轮（按目录名倒序，名称含时间戳）。
if [[ -d "$ROOT/evidence/e2e" ]]; then
  while IFS= read -r old; do
    [[ -n "$old" ]] || continue
    add_path "旧 E2E 轮次" "$old" "保留最近 5 轮"
  done < <(
    find "$ROOT/evidence/e2e" -mindepth 1 -maxdepth 1 -type d |
      while IFS= read -r d; do basename "$d"; done |
      sort -r | tail -n +6 |
      while IFS= read -r d; do printf '%s\n' "$ROOT/evidence/e2e/$d"; done
  )
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

if [[ "$WITH_PACKAGES" -eq 1 && -d "$ROOT/evidence/packages" ]]; then
  while IFS= read -r entry; do
    [[ -n "$entry" ]] || continue
    add_path "包布局验证" "$entry" "一次性产物"
  done < <(find "$ROOT/evidence/packages" -mindepth 1 -maxdepth 1)
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
