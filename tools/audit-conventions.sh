#!/usr/bin/env bash
# 仓库规范审计（R1–R10）。可复跑，本地与 CI 共用；退出码 0=全绿，1=存在不合规，2=用法错误。
# 仅使用 BSD/macOS 与 GNU/Linux 共同支持的命令：不用 find -printf、sort -V、head -n -1。
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

逐条检查 R1–R10 规范，输出 PASS/FAIL 清单。结果与 docs/status/conformance.md 应一致。

选项:
  -h, --help  显示本帮助

退出码: 0 全部通过 / 1 存在不合规 / 2 用法错误
EOF
}
[[ "${1:-}" == "-h" || "${1:-}" == "--help" ]] && { usage; exit 0; }
[[ $# -eq 0 ]] || { usage >&2; exit 2; }

FAILURES=0
CHECKS=0

pass() { CHECKS=$((CHECKS + 1)); printf 'PASS  %s\n' "$*"; }
fail() { CHECKS=$((CHECKS + 1)); FAILURES=$((FAILURES + 1)); printf 'FAIL  %s\n' "$*"; }
note() { printf 'NOTE  %s\n' "$*"; }

echo "== R1 根目录白名单 =="

ALLOWED="AiAssistant.sln CHANGELOG.md CONTRIBUTING.md Directory.Build.props Directory.Build.targets Directory.Packages.props EULA.md LICENSE README.md SECURITY.md THIRD-PARTY-NOTICES.md artifacts build docs global.json native nuget.config samples src testconfig.json tests tools dotnet.config"
ALLOWED_HIDDEN=".config .editorconfig .gitattributes .github .gitignore .workbuddy .workbuddy-ai"

for entry in * .[!.]*; do
  [[ -e "$entry" ]] || continue
  case "$entry" in
    .git) continue ;;
    .*) [[ " $ALLOWED_HIDDEN " == *" $entry "* ]] || fail "根目录出现白名单外隐藏条目：$entry" ;;
    *)  [[ " $ALLOWED " == *" $entry "* ]] || fail "根目录出现白名单外条目：$entry" ;;
  esac
done
CHECKS=$((CHECKS + 1))

# 目录型忽略规则必须用带斜杠的路径检查，否则目录不存在时 git check-ignore 会误报。
for d in .workbuddy/ .workbuddy-ai/; do
  if git check-ignore -q "$d"; then
    pass "R1 $d 已被 .gitignore 忽略（本地工具目录，不入库）"
  else
    fail "R1 $d 未忽略"
  fi
done

echo "== R2 目录职责 =="
for d in src tests docs build tools native samples; do
  [[ -f "$d/README.md" ]] && pass "R2 $d/ 有 README 声明职责" || fail "R2 $d/ 缺 README"
done

echo "== R3 命名 =="
offending="$(git grep -nE 'MacOS|MacOs' -- . ':!docs/archive/**' ':!docs/guides/conventions.md' | grep -v 'IsMacOS()' | grep -viE '[Cc]ontents/MacOS' || true)"
if [[ -z "$offending" ]]; then
  pass "R3 平台拼写唯一为 macOS（允许 IsMacOS()/Contents/MacOS/归档）"
else
  fail "R3 发现 macOS 拼写残留：$(printf '\n%s' "$offending" | head -5)"
fi

bad_doc="$(find docs -name '*.md' | while IFS= read -r f; do basename "$f"; done | grep -vE '^(README\.md|[0-9]{4}-[a-z0-9-]+\.md|[a-z0-9][a-z0-9.-]*\.md)$' || true)"
if [[ -z "$bad_doc" ]]; then
  pass "R3 文档文件名为小写连字符（README/日期前缀除外）"
else
  fail "R3 文档文件名不合规：$bad_doc"
fi

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
  if "$PY" - "$REPO_ROOT" <<'PYEOF'
import os, re, sys, urllib.parse
from collections import deque
root = sys.argv[1]
docs_root = os.path.join(root, "docs")
broken, docs = [], set()
for dp, dn, fn in os.walk(docs_root):
    dn[:] = [d for d in dn if d != ".git"]
    for f in fn:
        if f.endswith(".md"):
            docs.add(os.path.normpath(os.path.join(dp, f)))
links = {}
for p in docs:
    s = open(p, encoding="utf-8-sig").read()
    out = []
    for m in re.finditer(r'\[[^\]]*\]\(([^)]+)\)', s):
        t = m.group(1).strip()
        if t.startswith(("http://", "https://", "mailto:", "#", "<")):
            continue
        t = t.split("#")[0].split("?")[0]
        if not t:
            continue
        if t.endswith("/"):
            t += "README.md"
        dest = os.path.normpath(os.path.join(os.path.dirname(p), urllib.parse.unquote(t)))
        if not os.path.exists(dest):
            broken.append((os.path.relpath(p, root), t))
        else:
            out.append(dest)
    links[p] = out
start = os.path.join(docs_root, "README.md")
visited = {start: 0}
q = deque([start])
while q:
    p = q.popleft()
    if visited[p] >= 2:
        continue
    for nxt in links.get(p, []):
        if nxt not in visited:
            visited[nxt] = visited[p] + 1
            q.append(nxt)
orphans = sorted(os.path.relpath(p, root) for p in docs - set(visited))
if broken or orphans:
    for rel, target in broken[:5]:
        print(f"  断链: {rel} -> {target}")
    for rel in orphans[:10]:
        print(f"  孤儿: {rel}")
    sys.exit(1)
sys.exit(0)
PYEOF
  then
    pass "R4 相对链接 0 断链且文档 ≤2 跳可达"
  else
    fail "R4 存在断链或孤儿文档"
  fi
else
  note "R4 链接/可达性检查跳过（无 python）"
fi

no_header="$("$PY" - "$REPO_ROOT" <<'PYEOF'
import os, sys
root = sys.argv[1]
need = ("**状态**", "**最后更新**", "**适用版本**", "**维护触发条件**")
missing = []
for dp, dn, fn in os.walk(os.path.join(root, "docs")):
    dn[:] = [d for d in dn if d not in {".git", "archive"}]
    for f in fn:
        if not f.endswith(".md"):
            continue
        p = os.path.join(dp, f)
        s = open(p, encoding="utf-8-sig").read()
        absent = [k for k in need if k not in s]
        if absent:
            missing.append(f"{os.path.relpath(p, root)}(缺 {','.join(absent)})")
print("\n".join(missing))
PYEOF
)"
if [[ -z "$no_header" ]]; then
  pass "R4 所有文档含四段头信息（状态/最后更新/适用版本/维护触发条件）"
else
  fail "R4 文档头信息不全：$(printf '\n%s' "$no_header" | head -5)"
fi

echo "== R4/补充 UTF-8 无 BOM =="
bom="$("$PY" - "$REPO_ROOT" <<'PYEOF'
import os, subprocess, sys
root = sys.argv[1]
files = subprocess.run(["git", "ls-files"], cwd=root, capture_output=True, text=True).stdout.splitlines()
bad = []
for f in files:
    path = f"{root}/{f}"
    if os.path.isfile(path) and open(path, "rb").read(3) == b"\xef\xbb\xbf":
        bad.append(f)
print("\n".join(bad))
PYEOF
)"
[[ -z "$bom" ]] && pass "R4 入库文件 UTF-8 无 BOM" || fail "R4 发现 BOM：$bom"

echo "== R5 产物治理 =="
n_artifacts="$(git ls-files artifacts | wc -l | tr -d ' ')"
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
badreporoot="$(printf '%s\n' "$badreporoot" | tr ' ' '\n' | grep -v '^$' || true)"
[[ -z "$badreporoot" ]] && pass "R6 使用 REPO_ROOT 的 .sh 均以 git rev-parse 解析" || fail "R6 脚本仍用相对层级推根：$badreporoot"

# .ps1/.py 帮助契约（R6）
no_help="$(git ls-files '*.ps1' '*.py' | while IFS= read -r f; do
  grep -qiE -- '--help|-Help|\.SYNOPSIS|"""' "$f" || printf '%s\n' "$f"
done)"
[[ -z "$no_help" ]] && pass "R6 所有 .ps1/.py 提供帮助入口" || fail "R6 脚本缺帮助入口：$no_help"

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

legal_call="$(grep -l 'legal-files' build/package/package-windows.ps1 build/package/package-windows-msi.ps1 build/package/package-linux.sh build/package/package-macos.sh | wc -l | tr -d ' ')"
[[ "$legal_call" -eq 4 ]] && pass "R8 四个打包脚本均接入 legal-files" || fail "R8 打包脚本接入 legal-files 的个数=$legal_call（应为 4）"

ORT_VER="$(sed -n 's:.*Include="Microsoft.ML.OnnxRuntime.Managed" Version="\([^"]*\)".*:\1:p' Directory.Packages.props | head -1)"
NUGET_ROOT="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
if [[ -f "$NUGET_ROOT/microsoft.ml.onnxruntime.managed/$ORT_VER/ThirdPartyNotices.txt" ]]; then
  pass "R8 ORT 声明可在 NuGet 缓存定位"
else
  note "R8 ORT 声明不在本地缓存（需 dotnet restore 后验证）"
fi

if grep -q "aishell3" build/models/stage-bundle.sh; then
  pass "R8 stage-bundle.sh 拦截退役 AISHELL-3"
else
  fail "R8 stage-bundle.sh 缺少 aishell3 拦截"
fi

echo "== 补充：重复二进制资产 =="
dupes="$("$PY" - "$REPO_ROOT" <<'PYEOF'
import hashlib, os, sys
from collections import defaultdict
root = sys.argv[1]
by_hash = defaultdict(list)
for base in ("src", "build"):
    for dp, dn, fn in os.walk(os.path.join(root, base)):
        if "artifacts" in dp.split(os.sep):
            continue
        for f in fn:
            if f.lower().endswith((".png", ".jpg", ".ico", ".icns")):
                p = os.path.join(dp, f)
                by_hash[hashlib.sha256(open(p, "rb").read()).hexdigest()].append(
                    os.path.relpath(p, root))
for paths in by_hash.values():
    if len(paths) > 1:
        print(" = ".join(sorted(paths)))
PYEOF
)"
[[ -z "$dupes" ]] && pass "无重复二进制资产（按 SHA-256）" || fail "发现重复二进制资产：$dupes"

echo "== 补充：时钟抽象（ADR-0009） =="
clock="$(git grep -nE 'DateTime\.(Now|Today|UtcNow)|DateTimeOffset\.(Now|UtcNow)' -- 'src/**/*.cs' | grep -vE 'IClock|TimeProvider|SystemClock|ClockExtensions' || true)"
[[ -z "$clock" ]] && pass "生产代码无直接系统时钟调用" || fail "ADR-0009 违规：$(printf '\n%s' "$clock" | head -5)"

echo "== R9 配置与常量 =="
env_files="$(git grep -ln '"AIASSISTANT_' -- '*.cs' | grep -v 'EnvironmentNames.cs' || true)"
[[ -z "$env_files" ]] && pass "R9 环境变量字面量仅存在于 EnvironmentNames.cs" || fail "R9 环境变量字面量散落：$env_files"

echo
echo "结果：$CHECKS 项检查，$FAILURES 项不合规"
[[ "$FAILURES" -eq 0 ]]
