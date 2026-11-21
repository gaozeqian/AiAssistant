# AiAssistant 0.1.0 交付说明

## 安装包（由 GitHub Actions 生成）

| 平台 | 安装包 | 签名与信任 |
|---|---|---|
| Windows x64 | `AiAssistant-0.1.0-win-x64.msi` | 自签名 Authenticode；MSI 安装时以管理员权限运行 `certutil`，自动导入证书到本机 Root/TrustedPeople |
| macOS arm64 | `AiAssistant-0.1.0-osx-arm64.pkg` | 自签名 codesign；PKG `postinstall` 以 root 运行 `security add-trusted-cert`，自动导入证书 |
| Linux x64 | `AiAssistant-0.1.0-linux-x64.deb` | 未签名；dpkg 默认不强制签名 |

Release 同时附 `*.cer` 公钥证书、`bom.json` 与 `SHA256SUMS`，便于审计与校验。

## 安装

- **Windows**：双击 MSI，UAC 确认后安装到 `C:/Program Files\AiAssistant`，开始菜单创建「时光」快捷方式。
- **macOS**：双击 PKG，输入管理员密码；安装到 `/Applications/AiAssistant.app`，并信任自签名证书。
- **Linux**：
  ```bash
  sudo apt install ./AiAssistant-0.1.0-linux-x64.deb
  ```

## 自签名说明

- `.cer` 仅包含公钥，不含私钥；私钥只存在于构建 runner 的临时证书存储/钥匙串。
- 安装器会自动导入证书；这会把自签名根证书加入系统信任库，属于高权限操作，UAC/密码提示即用户确认。
- macOS 版本未公证；如 Gatekeeper 仍拦截，右键「打开」或在「隐私与安全性」中允许。

## 校验

```bash
sha256sum -c SHA256SUMS
```

## 数据与卸载

- 数据默认位于 `%LOCALAPPDATA%\AiAssistant`（Windows）、`~/Library/Application Support/AiAssistant`（macOS）、`~/.local/share/AiAssistant`（Linux）。
- 卸载不会自动删除数据；删除请使用应用内「隐私与控制 → 删除数据」，或手动删除上述目录。

## 已知限制

- 自签名证书不被系统默认信任，安装器会自动导入；企业环境如需禁止修改信任库，请改用真实 CA 证书。
- macOS 未公证；Windows SmartScreen 可能对未信誉证书提示。
- Linux `.deb` 依赖 `libsecret-1-0`、`libx11-6`、`libice6`、`libsm6`、`libfontconfig1`；推荐 `xdg-desktop-portal` 与 `pipewire`。
