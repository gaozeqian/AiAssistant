# 发布打包

跨平台发布脚本，产物统一落在 `artifacts/packages/`（已被 `.gitignore` 忽略）。

## Windows

```powershell
pwsh build/package/package-windows.ps1 -Version 0.1.0
```

- 默认 `win-x64` 自包含发布，输出 `AiAssistant-<version>-win-x64.zip`。
- 签名：设置 `AIASSISTANT_SIGN_CERT_THUMBPRINT`（证书指纹）与可选 `AIASSISTANT_TIMESTAMP_URL`；脚本会签名并校验全部 `.exe`/`.dll`。**指定了指纹但找不到 `signtool.exe` 时脚本会失败**，不会静默产出未签名包。
- MSIX：追加 `-Msix -Publisher "CN=你的发布者"`，需要 Windows SDK 的 `makeappx.exe`；清单模板见 `windows/Package.appxmanifest.template`。清单已声明 `unvirtualizedResources` 并关闭 AppData 虚拟化，数据写入真实 `%LOCALAPPDATA%\AiAssistant`，卸载不会删除。
- 自签名演练（不用于正式发布）：

  ```powershell
  $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=AiAssistant Test" `
      -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy Exportable
  Export-Certificate -Cert $cert -FilePath "$env:TEMP\AiAssistantTest.cer"
  certutil -user -addstore -f Root "$env:TEMP\AiAssistantTest.cer"
  certutil -user -addstore -f TrustedPeople "$env:TEMP\AiAssistantTest.cer"
  $env:AIASSISTANT_SIGN_CERT_THUMBPRINT = $cert.Thumbprint
  pwsh build/package/package-windows.ps1 -Msix -RequireSigning -SkipTimestamp -Publisher "CN=AiAssistant Test"
  ```

  `-SkipTimestamp` 仅用于离线自签名；正式发布必须使用时间戳。非管理员无法把证书加入机器级信任时，可用 `Add-AppxPackage -Register artifacts/packages/staging/msix-win-x64/AppxManifest.xml` 做布局/启动/卸载演练（不验证安装签名）。
- 图标由 `assets/generate-icon.py` 生成（纯 Python，无第三方依赖）：`python build/package/assets/generate-icon.py`。

## macOS

```bash
# 布局/CI 冒烟（不签名）
bash build/package/package-macos.sh --skip-sign

# 正式发布（需在 macOS 上执行）
export AIASSISTANT_CODESIGN_IDENTITY="Developer ID Application: Example (TEAMID)"
export AIASSISTANT_NOTARY_PROFILE="aiassistant-notary"   # xcrun notarytool store-credentials 创建
bash build/package/package-macos.sh
```

- 先构建 Swift Helper：`cd native/AiAssistant.MacHelper && swift build -c release`。
- 产出 `AiAssistant.app`（`Contents/MacOS` 内含 Helper）与 `AiAssistant-<version>-osx-arm64.zip`。
- 使用 hardened runtime + `macos/AiAssistant.entitlements`（JIT、音频输入）；有公证 profile 时自动 `notarytool submit --wait` 并 `stapler staple`。

## Linux

```bash
bash build/package/package-linux.sh
```

- 产出便携 `AiAssistant-<version>-linux-x64.tar.gz`（含 `.desktop` 与图标）。
- 安装 `dpkg-deb` 时额外产出 `.deb`（`usr/lib/aiassistant`、`usr/bin/aiassistant`、桌面入口）。
- 依赖：`libsecret-1-0`、`libx11-6`、`libfontconfig1`；Wayland 会话建议安装 `xdg-desktop-portal` 与 `pipewire`。

## CI

`.github/workflows/ci.yml` 的 `package` 作业在三平台分别运行上述脚本（默认不签名）并上传产物；签名密钥通过仓库 Secrets 注入后自动启用。
