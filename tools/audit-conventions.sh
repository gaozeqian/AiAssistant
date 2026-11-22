#!/usr/bin/env bash
# 仓库规范审计（R1–R9）。可复跑，本地与 CI 共用。退出码 0=全绿，1=存在不合规。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null || true)"
if [[ -z "$REPO_ROOT" ]]; then
  echo "错误：无法定位仓库根目录（$SCRIPT_DIR 不在 Git 工作区内）。" >&2
  exit 1
fi

cd "$REPO_ROOT"

usage() {
  cat <<'EOF'
用法: bash tools/audit-conventions.sh [选项]

逐条检查 R1–R9 规范，输出 PASS/FAIL 清单。结果与 docs/status/conformance.md 应一致。

选项:
  -h, --help  显示本帮助

退出码: 0 全部通过 / 1 存在不合规 / 2 用法错误
EOF
}

[[ "${1:-}" == "-h" || "${1:-}" == "--help" ]] && { usage; exit 0; }

FAILURES=0
CHECKS=0

pass() { CHECKS=$((CHECKS + 1)); printf 'PASS  %s\n' "$*"; }
fail() { CHECKS=$((CHECKS + 1)); FAILURES=$((FAILURES + 1)); printf 'FAIL  %s\n' "$*"; }
note() { printf 'NOTE  %s\n' "$*"; }

check() {
  local label="$1"; shift
  if "$@" >/dev/null 2>&1; then pass "$label"; else fail "$label"; fi
}

echo "== R1 根目录白名单 =="

ALLOWED="AiAssistant.sln CHANGELOG.md CONTRIBUTING.md Directory.Build.props Directory.Packages.props EULA.md LICENSE README.md SECURITY.md THIRD-PARTY-NOTICES.md artifacts build docs global.json native samples src tests tools"
ALLOWED_HIDDEN=".config .editorconfig .gitattributes .github .gitignore .workbuddy"

for entry in $(ls -1a); do
  case "$entry" in
    .|..|.git) continue ;;
  esac
  case "$entry" in
    .*) [[ " $ALLOWED_HIDDEN " == *" $entry "* ]] || fail "根目录出现白名单外隐藏条目：$entry" ;;
    *)  [[ " $ALLOWED " == *" $entry "* ]] || fail "根目录出现白名单外条目：$entry" ;;
  esac
done
CHECKS=$((CHECKS + 1))

if git check-ignore -q .workbuddy; then
  pass "R1 .workbuddy/ 已被 .gitignore 忽略"
else
  fail "R1 .workbuddy/ 未忽略"
fi

echo "== R2 目录职责 =="
for d in src tests docs build tools native samples; do
  [[ -f "$d/README.md" ]] && pass "R2 $d/ 有 README 声明职责" || fail "R2 $d/ 缺 README"
done

echo "== R3 命名 =="
# macOS 唯一拼写：排除 BCL API 与 Apple bundle 布局，归档目录允许。
offending="$(git grep -nE 'MacOS|MacOs' -- . ':!docs/archive/**' ':!docs/guides/conventions.md' | grep -v 'IsMacOS()' | grep -viE '[Cc]ontents/MacOS' || true)"
if [[ -z "$offending" ]]; then
  pass "R3 平台拼写唯一为 macOS（允许 IsMacOS()/Contents/MacOS/归档）"
else
  fail "R3 发现 macOS 拼写残留：$(printf '\n%s' "$offending" | head -5)"
fi

# 文档文件名小写连字符（README 与日期前缀除外）
bad_doc="$(find docs -name '*.md' -printf '%P\n' | grep -vE '^(README\.md|[0-9]{4}-[a-z0-9-]+\.md|[a-z0-9]+(-[a-z0-9]+)*\.md)$' | grep -vE '^[a-z0-9-]+/' || true)"
# 去掉目录路径，只判断文件名
bad_doc="$(find docs -name '*.md' -printf '%f\n' | grep -vE '^(README\.md|[0-9]{4}-[a-z0-9-]+\.md|[a-z0-9][a-z0-9.-]*\.md)$' || true)"
if [[ -z "$bad_doc" ]]; then
  pass "R3 文档文件名为小写连字符（README/日期前缀除外）"
else
  fail "R3 文档文件名不合规：$bad_doc"
fi

# .sh 权限位一致 755
badperm="$(git ls-files -s '*.sh' | awk '$1 != "100755" { print $4 }' || true)"
if [[ -z "$badperm" ]]; then
  pass "R3/R6 所有 .sh 权限位 100755"
else
  fail "R3/R6 .sh 权限位不是 755：$badperm"
fi

echo "== R4 文档体系 =="
[[ -f docs/README.md ]] && pass "R4 存在 docs/README.md 唯一索引" || fail "R4 缺 docs/README.md"

PY="$(command -v python3 || command -v python || true)"
if [[ -n "$PY" ]]; then
  "$PY" - "$REPO_ROOT" <<'PYEOF'
import os, re, sys, urllib.parse
root = sys.argv[1]
broken = []
seen = set()
for dp, dn, fn in os.walk(os.path.join(root, "docs")):
    dn[:] = [d for d in dn if d not in {".git"}]
    for f in fn:
        if not f.endswith(".md"): continue
        p = os.path.join(dp, f)
        seen.add(p)
        s = open(p, encoding="utf-8").read()
        for m in re.finditer(r'\[[^\]]*\]\(([^)]+)\)', s):
            t = m.group(1).strip()
            if t.startswith(("http://", "https://", "mailto:", "#", "<")): continue
            t = t.split("#")[0].split("?")[0]
            if not t: continue
            if t.endswith("/"): t += "README.md"
            dest = os.path.normpath(os.path.join(dp, urllib.parse.unquote(t)))
            if not os.path.exists(dest):
                broken.append((os.path.relpath(p, root), t))
sys.exit(1 if broken else 0)
PYEOF
  if [[ $? -eq 0 ]]; then pass "R4 相对链接 0 断链"; else fail "R4 存在断链"; fi
else
  note "R4 链接检查跳过（无 python）"
fi

# 每篇文档有头信息（状态字段）
no_header="$(grep -rL '\*\*状态\*\*' docs --include='*.md' | grep -v 'archive/' || true)"
if [[ -z "$no_header" ]]; then
  pass "R4 所有文档含头信息（状态字段）"
else
  fail "R4 文档缺头信息：$(printf '\n%s' "$no_header" | head -5)"
fi

echo "== R5 产物治理 =="
n_artifacts="$(git ls-files artifacts | wc -l)"
[[ "$n_artifacts" -eq 0 ]] && pass "R5 artifacts/ 不入库（git ls-files = 0）" || fail "R5 artifacts/ 有 $n_artifacts 个入库文件"

hardcoded_cs="$(git grep -n '"artifacts"' -- '*.cs' | grep -v 'DefaultRootName' || true)"
[[ -z "$hardcoded_cs" ]] && pass "R5 代码无 'artifacts' 字面量（集中于 ArtifactPaths）" || fail "R5 代码存在 'artifacts' 字面量：$(printf '\n%s' "$hardcoded_cs" | head -3)"

[[ -f tools/clean-artifacts.sh ]] && pass "R5 存在清理入口 tools/clean-artifacts.sh" || fail "R5 缺清理入口"

echo "== R6 构建与工具 =="
[[ -f .config/dotnet-tools.json ]] && pass "R6 dotnet-tools.json 位于 .config/" || fail "R6 dotnet-tools.json 不在 .config/"
[[ ! -f dotnet-tools.json ]] && pass "R6 根目录无 dotnet-tools.json" || fail "R6 根目录残留 dotnet-tools.json"

badreporoot=""
while IFS= read -r f; do
  grep -q 'rev-parse --show-toplevel' "$f" || badreporoot="$badreporoot $f"
done < <(grep -rl 'REPO_ROOT' build tools --include='*.sh' | grep -vE 'README')
badreporoot="$(echo "$badreporoot" | xargs -n1 echo 2>/dev/null | grep -v '^$' || true)"
[[ -z "$badreporoot" ]] && pass "R6 使用 REPO_ROOT 的 .sh 均以 git rev-parse 解析" || fail "R6 脚本仍用相对层级推根：$badreporoot"

echo "== R7 CI =="
msix_wf="$(grep -rni 'msix' .github/workflows || true)"
[[ -z "$msix_wf" ]] && pass "R7 工作流无 MSIX 构建步骤" || fail "R7 工作流出现 MSIX：$(printf '\n%s' "$msix_wf" | head -3)"

msix_ps="$(grep -niE '\-Msix|makeappx|appxmanifest' build/package/package-windows.ps1 || true)"
[[ -z "$msix_ps" ]] && pass "R7 package-windows.ps1 无 MSIX 参数/分支" || fail "R7 package-windows.ps1 含 MSIX 步骤：$msix_ps"

echo "== R8 元文件与许可 =="
for f in LICENSE EULA.md THIRD-PARTY-NOTICES.md CHANGELOG.md CONTRIBUTING.md SECURITY.md .gitattributes; do
  [[ -f "$f" ]] && pass "R8 存在 $f" || fail "R8 缺 $f"
done
for f in .github/PULL_REQUEST_TEMPLATE.md .github/CODEOWNERS .github/dependabot.yml .github/ISSUE_TEMPLATE/bug_report.md; do
  [[ -f "$f" ]] && pass "R8 存在 $f" || fail "R8 缺 $f"
done

legal_call="$(grep -l 'legal-files' build/package/package-windows.ps1 build/package/package-windows-msi.ps1 build/package/package-linux.sh build/package/package-macos.sh | wc -l)"
[[ "$legal_call" -eq 4 ]] && pass "R8 四个打包脚本均接入 legal-files" || fail "R8 打包脚本接入 legal-files 的个数=$legal_call（应为 4）"

# ORT 声明可在 NuGet 缓存定位（无缓存时仅提示）
ORT_VER="$(sed -n 's:.*Include="Microsoft.ML.OnnxRuntime.Managed" Version="\([^"]*\)".*:\1:p' Directory.Packages.props | head -1)"
NUGET_ROOT="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
if [[ -f "$NUGET_ROOT/microsoft.ml.onnxruntime.managed/$ORT_VER/ThirdPartyNotices.txt" ]]; then
  pass "R8 ORT 声明可在 NuGet 缓存定位"
else
  note "R8 ORT 声明不在本地缓存（需 dotnet restore 后验证）"
fi

# 模型白名单：stage-bundle 必须拒绝 aishell3
if grep -q "aishell3" build/models/stage-bundle.sh; then
  pass "R8 stage-bundle.sh 拦截退役 AISHELL-3"
else
  fail "R8 stage-bundle.sh 缺少 aishell3 拦截"
fi

echo "== R9 配置与常量 =="
env_files="$(git grep -ln '"AIASSISTANT_' -- '*.cs' | grep -v 'EnvironmentNames.cs' || true)"
[[ -z "$env_files" ]] && pass "R9 环境变量字面量仅存在于 EnvironmentNames.cs" || fail "R9 环境变量字面量散落：$env_files"

echo
echo "结果：$CHECKS 项检查，$FAILURES 项不合规"
[[ "$FAILURES" -eq 0 ]]
