# 三平台交付矩阵

> **状态**：living ｜ **最后更新**：2026-11-22 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：交付格式、签名方式或平台支持变化时

| 平台 | 交付物 | 构建位置 | 签名 | 证书信任方式 |
|---|---|---|---|---|
| Windows x64 | `AiAssistant-<ver>-win-x64.zip` + `.msi` | GitHub Actions `windows-latest` | 正式 CA Authenticode；开发可选自签名 | 仅 `--dev-self-signed` 时 MSI custom action 调用 `certutil` 导入 Root/TrustedPeople；正式包不改信任库 |
| macOS arm64 | `AiAssistant-<ver>-osx-arm64.pkg` + `.zip` | GitHub Actions `macos-latest` | Developer ID + 公证；开发可选自签名 | 仅 `--dev-self-signed` 时 PKG `postinstall` 调用 `security add-trusted-cert`；正式包不改信任库 |
| Linux x64 | `AiAssistant-<ver>-linux-x64.tar.gz` + `.deb` | GitHub Actions `ubuntu-latest` | 未签名 | dpkg 默认不强制签名，无需证书 |

## 发布流程

1. 推送 `v*` tag（正式 CA / Developer ID 路径）或手动触发 `.github/workflows/release.yml`。
2. 手动触发时必须**显式勾选** `dev_self_signed` 才会构建自签名版本并导入本地信任根；tag 推送即使误传该输入也不生效。
3. 三个 job 分别在对应 runner 上构建、测试、签名，并同时产出 macOS `.zip` 与 Linux `.tar.gz`。
4. `publish` job 汇总安装包、`.cer`（仅开发模式）、`bom.json`，生成 `SHA256SUMS`。
5. 创建 **draft + prerelease** GitHub Release；人工检查后发布。

## 本地构建

- Windows zip：`pwsh build/package/package-windows.ps1 -Version 0.1.0`
- Windows MSI（开发自签名）：`pwsh build/package/package-windows-msi.ps1 -Version 0.1.0 -CertificatePath <cer> -DevSelfSigned`
- macOS PKG + zip（开发自签名）：`bash build/package/package-macos.sh --version 0.1.0 --require-sign --dev-self-signed --certificate <cer>`
- Linux tar.gz + deb：`bash build/package/package-linux.sh --version 0.1.0`

## 安全说明

- `.cer` 只含公钥；私钥仅存在于构建 runner 的临时证书存储/钥匙串。
- 安装器导入自签名根证书属于高权限操作，**只在显式 `--dev-self-signed` 模式下发生**；正式 CA / Developer ID 路径物理隔离。
- 手动触发 Release 时默认勾选 `dev_self_signed`，修改系统信任库前仍需 UAC / 管理员密码确认。
- 企业环境如禁止修改系统信任库，应使用 tag 推送的正式签名路径。
