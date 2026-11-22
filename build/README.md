# build/ — 构建、打包、签名与 SBOM

本目录只放构建流水线必需的东西。开发辅助客户端与日常运维脚本放 `tools/`。

## 布局

| 路径 | 职责 |
|---|---|
| `models/fetch-bundle.sh` | 下载并展开 sherpa-onnx 模型资产 |
| `models/stage-bundle.sh` | 汇总随包模型载荷到 `artifacts/models/bundle` |
| `package/package-windows.ps1` | Windows 自包含 zip（可选签名） |
| `package/package-windows-msi.ps1` | Windows MSI（WiX，可选签名） |
| `package/package-macos.sh` | macOS `.app` / `.zip` / `.pkg`（codesign + 可选公证） |
| `package/package-linux.sh` | Linux `tar.gz` / `.deb` |
| `package/legal-files.sh` / `.ps1` | 四个法务文件随包（缺失即失败） |
| `package/macos/create-self-signed-identity.sh` | 演练用自签名身份 |
| `package/assets/` | 图标与图标生成脚本 |
| `sbom/generate-sbom.py` | 离线 CycloneDX SBOM 与校验 |

各 domain 的详细说明见同目录 `README.md`。

## 脚本约定

所有 `.sh` 遵循同一骨架，新增脚本请复制该结构：

```bash
#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null || true)"
if [[ -z "$REPO_ROOT" ]]; then
  echo "错误：无法定位仓库根目录（$SCRIPT_DIR 不在 Git 工作区内）。" >&2
  exit 1
fi
```

- 禁止用 `../..` 推断仓库根；禁止依赖脚本自身所在目录深度。
- 必须提供 `--help`；破坏性操作必须提供 `--dry-run` 且默认 dry-run。
- 退出码：`0` 成功、`1` 一般失败、`2` 用法错误。
- `.sh` 权限位 755；`.ps1` 保存为 UTF-8 无 BOM。

## 产物

构建与打包产物一律落在 `artifacts/`（不入库）。目录分层与保留策略见 [docs/guides/artifacts.md](../docs/guides/artifacts.md)。
