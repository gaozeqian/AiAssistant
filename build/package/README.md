# build/package — 跨平台打包脚本

打包脚本把 `artifacts/publish/<rid>/` 的共享载荷组装为 `artifacts/staging/<rid>-<format>/`，
最终产物统一写入 `artifacts/dist/<version>/`。`artifacts/` 已被 `.gitignore` 忽略。

| 脚本 | 产物 |
|---|---|
| `package-windows.ps1` | `AiAssistant-<ver>-windows-x64-portable[-dev].zip`（顶层目录 + 一次 publish/签名） |
| `package-windows-msi.ps1` | `AiAssistant-<ver>-windows-x64[-dev].msi`（复用同一 publish 载荷） |
| `package-macos.sh` | `AiAssistant-<ver>-macos-arm64.app.zip` / `.pkg`（`-dev` 表示开发自签名） |
| `package-linux.sh` | `aiassistant-<ver>-linux-x64.tar.gz`（顶层目录）/ `aiassistant_<ver>_amd64.deb` |
| `checksums.sh` / `checksums.ps1` | 单包 `<file>.sha256` + `AiAssistant-<ver>-SHA256SUMS.txt` |

## 前置

- 先构建 Swift Helper：`bash build/native/mac-helper.sh build`（产物在 `artifacts/obj/native/mac-helper/`）。
- 随包模型：`bash build/models/fetch-bundle.sh` 后再 `stage-bundle.sh`；CI 布局验证可传 `--skip-models` / `-SkipModelBundle`。
- Windows 便携包与 MSI 共享 `artifacts/publish/win-x64`，必须先运行 `package-windows.ps1` 再运行 MSI 脚本。
