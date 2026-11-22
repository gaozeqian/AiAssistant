# 三平台交付矩阵

| 平台 | 安装包 | 构建位置 | 签名 | 证书信任方式 |
|---|---|---|---|---|
| Windows x64 | `AiAssistant-<ver>-win-x64.zip` + `.msi` | GitHub Actions `windows-latest` | 自签名 Authenticode | MSI 提权 custom action 调用 `certutil` 导入本机 Root/TrustedPeople；zip 解压即用 |
| macOS arm64 | `AiAssistant-<ver>-osx-arm64.pkg` | GitHub Actions `macos-latest` | 自签名 codesign | PKG `postinstall` 以 root 调用 `security add-trusted-cert` |
| Linux x64 | `AiAssistant-<ver>-linux-x64.deb` | GitHub Actions `ubuntu-latest` | 未签名 | dpkg 默认不强制签名，无需证书 |

## 发布流程

1. 推送 `v*` tag 或手动触发 `.github/workflows/release.yml`。
2. 三个 job 分别在对应 runner 上构建、测试、签名。
3. `publish` job 汇总安装包、`.cer`、`bom.json`，生成 `SHA256SUMS`。
4. 创建 **draft + prerelease** GitHub Release；人工检查后发布。

## 本地构建

- Windows zip：`pwsh build/package/package-windows.ps1 -Version 0.1.0`
- Windows MSI：`.\build\package\package-windows-msi.ps1 -Version 0.1.0 -CertificatePath <cer>`
- Linux deb：`bash build/package/package-linux.sh --version 0.1.0`
- macOS PKG：仅 macOS，`bash build/package/package-macos.sh --version 0.1.0 --require-sign --certificate <cer>`

## 安全说明

- `.cer` 只含公钥；私钥仅存在于构建 runner 的临时证书存储/钥匙串。
- 安装器自动导入自签名根证书属于高权限操作，UAC/管理员密码即用户确认。
- 企业环境如禁止修改系统信任库，应改用真实 CA 证书。
