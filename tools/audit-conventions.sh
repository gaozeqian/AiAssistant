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

逐条检查 R1–R10 与补充规范，输出 PASS/FAIL 清单。

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

ALLOWED="AiAssistant.sln CHANGELOG.md CONTRIBUTING.md Directory.Build.props Directory.Build.targets Directory.Packages.props EULA.md LICENSE README.md SECURITY.md THIRD-PARTY-NOTICES.md artifacts build docs global.json native nuget.config samples src tests tools dotnet.config"
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
  if [[ -f "$d/README.md" ]]; then
    pass "R2 $d/ 有 README 声明职责"
  else
    fail "R2 $d/ 缺 README"
  fi
done

echo "== R3 命名 =="
offending="$(git grep -nE 'MacOS|MacOs' -- . | grep -v 'IsMacOS()' | grep -viE '[Cc]ontents/MacOS' || true)"
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
if [[ -f docs/README.md ]]; then
  pass "R4 存在 docs/README.md 唯一索引"
else
  fail "R4 缺 docs/README.md"
fi

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
start = os.path.normpath(os.path.join(docs_root, "README.md"))
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

doc_count="$(find docs -name '*.md' | wc -l | tr -d ' ')"
if [[ "$doc_count" -le 26 ]]; then
  pass "文档收敛为 $doc_count 篇（≤26）"
else
  fail "文档数量反弹：${doc_count}（应 ≤26）"
fi

stale_doc_dirs="$(find docs -maxdepth 1 -type d \( -name 'archive' -o -name 'status' -o -name 'testing' -o -name 'compliance' \) 2>/dev/null || true)"
if [[ -z "$stale_doc_dirs" ]]; then
  pass "无已删除文档目录（archive/status/testing/compliance）"
else
  fail "残留文档目录：$stale_doc_dirs"
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
if [[ -z "$bom" ]]; then
  pass "R4 入库文件 UTF-8 无 BOM"
else
  fail "R4 发现 BOM：$bom"
fi

echo "== R5 产物治理 =="
n_artifacts="$(git ls-files artifacts | wc -l | tr -d ' ')"
if [[ "$n_artifacts" -eq 0 ]]; then
  pass "R5 artifacts/ 不入库（git ls-files = 0）"
else
  fail "R5 artifacts/ 有 $n_artifacts 个入库文件"
fi

hardcoded_cs="$(git grep -n '"artifacts"' -- '*.cs' | grep -v 'DefaultRootName' || true)"
if [[ -z "$hardcoded_cs" ]]; then
  pass "R5 代码无 'artifacts' 字面量（集中于 ArtifactPaths）"
else
  fail "R5 代码存在 'artifacts' 字面量：$(printf '\n%s' "$hardcoded_cs" | head -3)"
fi

if [[ -f tools/clean-artifacts.sh ]]; then
  pass "R5 存在清理入口 tools/clean-artifacts.sh"
else
  fail "R5 缺清理入口"
fi

echo "== R6 构建与工具 =="
if [[ -f .config/dotnet-tools.json ]]; then
  pass "R6 dotnet-tools.json 位于 .config/"
else
  fail "R6 dotnet-tools.json 不在 .config/"
fi

if [[ ! -f dotnet-tools.json ]]; then
  pass "R6 根目录无 dotnet-tools.json"
else
  fail "R6 根目录残留 dotnet-tools.json"
fi

badreporoot=""
while IFS= read -r f; do
  grep -q 'rev-parse --show-toplevel' "$f" || badreporoot="$badreporoot $f"
done < <(grep -rl 'REPO_ROOT' build tools --include='*.sh' | grep -vE 'README')
badreporoot="$(printf '%s\n' "$badreporoot" | tr ' ' '\n' | grep -v '^$' || true)"
if [[ -z "$badreporoot" ]]; then
  pass "R6 使用 REPO_ROOT 的 .sh 均以 git rev-parse 解析"
else
  fail "R6 脚本仍用相对层级推根：$badreporoot"
fi

# .ps1/.py 帮助契约（R6）
no_help="$(git ls-files '*.ps1' '*.py' | while IFS= read -r f; do
  grep -qiE -- '--help|-Help|\.SYNOPSIS|"""' "$f" || printf '%s\n' "$f"
done)"
if [[ -z "$no_help" ]]; then
  pass "R6 所有 .ps1/.py 提供帮助入口"
else
  fail "R6 脚本缺帮助入口：$no_help"
fi

echo "== R7 CI =="
msix_wf="$(grep -rni 'msix' .github/workflows || true)"
if [[ -z "$msix_wf" ]]; then
  pass "R7 工作流无 MSIX 构建步骤"
else
  fail "R7 工作流出现 MSIX：$(printf '\n%s' "$msix_wf" | head -3)"
fi

msix_ps="$(grep -niE '\-Msix|makeappx|appxmanifest' build/package/package-windows.ps1 || true)"
if [[ -z "$msix_ps" ]]; then
  pass "R7 package-windows.ps1 无 MSIX 参数/分支"
else
  fail "R7 package-windows.ps1 含 MSIX 步骤：$msix_ps"
fi

echo "== R8 元文件与许可 =="
for f in LICENSE EULA.md THIRD-PARTY-NOTICES.md CHANGELOG.md CONTRIBUTING.md SECURITY.md .gitattributes; do
  if [[ -f "$f" ]]; then
    pass "R8 存在 $f"
  else
    fail "R8 缺 $f"
  fi
done
for f in .github/PULL_REQUEST_TEMPLATE.md .github/CODEOWNERS .github/dependabot.yml .github/ISSUE_TEMPLATE/bug_report.md; do
  if [[ -f "$f" ]]; then
    pass "R8 存在 $f"
  else
    fail "R8 缺 $f"
  fi
done

legal_call="$(grep -l 'legal-files' build/package/package-windows.ps1 build/package/package-windows-msi.ps1 build/package/package-linux.sh build/package/package-macos.sh | wc -l | tr -d ' ')"
if [[ "$legal_call" -eq 4 ]]; then
  pass "R8 四个打包脚本均接入 legal-files"
else
  fail "R8 打包脚本接入 legal-files 的个数=${legal_call}（应为 4）"
fi

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
if [[ -z "$dupes" ]]; then
  pass "无重复二进制资产（按 SHA-256）"
else
  fail "发现重复二进制资产：$dupes"
fi

echo "== 补充：时钟抽象（ADR-0009） =="
clock="$(git grep -nE 'DateTime\.(Now|Today|UtcNow)|DateTimeOffset\.(Now|UtcNow)' -- 'src/**/*.cs' | grep -vE 'IClock|TimeProvider|SystemClock|ClockExtensions' || true)"
if [[ -z "$clock" ]]; then
  pass "生产代码无直接系统时钟调用"
else
  fail "ADR-0009 违规：$(printf '\n%s' "$clock" | head -5)"
fi

timeprovider="$(git grep -n 'TimeProvider\.System' -- 'src/**/*.cs' | grep -v 'Ports/IClock.cs' || true)"
if [[ -z "$timeprovider" ]]; then
  pass "R9 生产代码仅 IClock 实现允许 TimeProvider.System"
else
  fail "R9 TimeProvider.System 绕过 IClock：$(printf '\n%s' "$timeprovider" | head -5)"
fi

echo "== R9 配置与常量 =="
env_files="$(git grep -ln '"AIASSISTANT_' -- '*.cs' | grep -v 'EnvironmentNames.cs' || true)"
if [[ -z "$env_files" ]]; then
  pass "R9 环境变量字面量仅存在于 EnvironmentNames.cs"
else
  fail "R9 环境变量字面量散落：$env_files"
fi

echo "== 补充：测试工程与源码树零污染 =="
stray_dirs="$(find src tests build tools native docs -type d \
  \( -name '__pycache__' -o -name '.build' -o -name 'test-temp' -o -name 'TestResults' -o -name 'bin' -o -name 'obj' \) \
  -not -path '*/obj/native/*' 2>/dev/null || true)"
if [[ -z "$stray_dirs" ]]; then
  pass "源码树无 __pycache__/.build/test-temp/bin/obj 逃逸目录"
else
  fail "源码树出现临时/编译目录：$stray_dirs"
fi

legacy_temp="$(find tests -name '*.cs' -not -path '*/AiAssistant.Testing.Common/*' -exec grep -nE 'AppContext\.BaseDirectory.*test-temp|"/tmp/|Path\.GetTempPath' {} + 2>/dev/null || true)"
if [[ -z "$legacy_temp" ]]; then
  pass "测试临时目录统一走 artifacts/tmp"
else
  fail "测试仍写旧临时目录：$(printf '\n%s' "$legacy_temp" | head -5)"
fi

missing_traits="$(git ls-files 'tests/**/*Tests.cs' | while IFS= read -r f; do
  grep -q '\[Trait("Category"' "$f" || printf '%s\n' "$f"
done)"
if [[ -z "$missing_traits" ]]; then
  pass "测试类均声明 Category Trait"
else
  fail "测试类缺 Category Trait：$missing_traits"
fi

project_count="$(find tests -name '*.csproj' | wc -l | tr -d ' ')"
if [[ "$project_count" -ge 15 ]]; then
  pass "测试工程登记数 ${project_count}（含 Testing.Common）"
else
  fail "测试工程数异常：$project_count"
fi

echo "== 补充：产物目录与命名契约 =="
legacy_paths="$(git grep -nE 'artifacts/(build|package|evidence)(/|")' -- . ':!docs/**' ':!artifacts/**' || true)"
if [[ -z "$legacy_paths" ]]; then
  pass "无旧产物目录引用（build/package/evidence）"
else
  fail "仍引用旧产物目录：$(printf '\n%s' "$legacy_paths" | head -5)"
fi

root_dist="$(find . -maxdepth 1 -type d -name 'dist' -not -path './artifacts*' 2>/dev/null || true)"
if [[ -z "$root_dist" ]]; then
  pass "仓库根无临时 dist/"
else
  fail "仓库根出现 dist/：$root_dist"
fi

wrong_bin="$(grep -nE 'AiAssistant\.Host(\.exe)?' build/package/windows/Package.msi.wxs build/package/macos/Info.plist.template build/package/linux/aiassistant.desktop 2>/dev/null || true)"
if [[ -z "$wrong_bin" ]]; then
  pass "平台清单引用发布态二进制 AiAssistant/aiassistant"
else
  fail "平台清单仍引用 AiAssistant.Host：$(printf '\n%s' "$wrong_bin" | head -5)"
fi

for f in build/package/checksums.sh build/package/checksums.ps1 tools/clean-artifacts.sh; do
  if [[ -f "$f" ]]; then
    pass "存在 $f"
  else
    fail "缺 $f"
  fi
done

echo
echo "结果：$CHECKS 项检查，$FAILURES 项不合规"
[[ "$FAILURES" -eq 0 ]]
