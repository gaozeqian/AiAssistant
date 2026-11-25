# 发布与部署

> **状态**：living ｜ **最后更新**：2026-11-24 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：交付矩阵、签名、安装/升级/回滚流程或 CI 发布作业变化时

本文件合并原 `release/delivery-matrix.md`、`release/release-checklist.md`、`release/upgrade-and-uninstall.md` 与 `release/rollback.md`。

## 1. 交付矩阵

| 平台 | 系统包 | 便携包 | 可执行文件 | 签名 |
|---|---|---|---|---|
| Windows x64 | `AiAssistant-<ver>-windows-x64.msi` | `AiAssistant-<ver>-windows-x64-portable.zip`（含顶层目录） | `AiAssistant.exe` | Authenticode；开发自签名带 `-dev` |
| macOS arm64 | `AiAssistant-<ver>-macos-arm64.pkg` | `AiAssistant-<ver>-macos-arm64.app.zip` | `AiAssistant` | Developer ID + notarization；开发自签名带 `-dev` |
| Linux x64 | `aiassistant_<ver>_amd64.deb` | `aiassistant-<ver>-linux-x64.tar.gz`（顶层目录） | `aiassistant` | 未签名 |

附带文件：

- `AiAssistant-<ver>-sbom.cdx.json`（CycloneDX 1.6）
- 每个分发包的 `<file>.sha256`
- `AiAssistant-<ver>-SHA256SUMS.txt`

全部汇聚在 `artifacts/dist/<version>/`。Windows 便携包与 MSI 共享 `artifacts/publish/win-x64`，只 publish/签名一次。

## 2. 版本与构建

- 版本唯一来源 `Directory.Build.props` 的 `VersionPrefix`；发布工作流也可显式传入。
- 构建：`dotnet build AiAssistant.sln -c Release --warnaserror`。
- 发布载荷：`dotnet publish ... -r <rid>` 默认写入 `artifacts/publish/<rid>/`，并生成 `publish-manifest.txt`。
- 打包脚本只组装 `staging/`，最终复制到 `dist/`；不修改源码树。

## 3. 签名与信任

**Windows**

- 正式版：真实 CA 证书指纹；`signtool` 对全部 `.exe/.dll` 与 MSI 签名；缺失签名工具时直接失败。
- 开发自签名：`-DevSelfSigned` 生成 `-dev` 产物；仅该模式允许向本机信任库导入根证书。

**macOS**

- 正式版：Developer ID 签名、hardened runtime、entitlements、notarytool 公证、stapler 装订。
- 开发自签名：`--dev-self-signed --certificate <cer>`；pkg postinstall 导入本地信任根，产物带 `-dev`。
- 正式路径绝不修改 runner 或用户信任库。

**Linux**

- 包不签名；通过 SHA-256 与 SBOM 校验完整性。deb 依赖见 `build/package/linux/control.template`。

## 4. CI 与 Release 工作流

`ci.yml`（三平台矩阵）：

1. restore → format 校验 → `build --warnaserror` → 全量测试（采样 + 覆盖率）。
2. macOS：`build/native/mac-helper.sh smoke` 与 `test`。
3. publish 验证：Swift Helper、Tmds.DBus 随包；覆盖率/浸泡/基准上传。
4. 打包布局验证（跳过模型与签名）。
5. SBOM/许可证与仓库规范审计。

`release.yml`（tag 或手动）：

1. 三平台分别 restore/test、构建 Helper、拉取随包模型、打包并签名。
2. Windows 先 portable 再 MSI（共享载荷）；macOS 生成 pkg/app.zip；Linux 生成 deb/tar.gz。
3. `publish` 作业生成 SBOM、汇总 checksums、创建 draft GitHub Release。
4. tag 推送走正式签名；手动触发可选择开发自签名（`-dev`）。

## 5. 安装

**Windows**

```powershell
msiexec /i AiAssistant-0.1.2-windows-x64.msi
# 或解压 portable zip 后直接运行 AiAssistant.exe
```

MSI 为 perMachine，需管理员；数据目录 `%LOCALAPPDATA%\AiAssistant` 默认保留。

**macOS**

```bash
sudo installer -pkg AiAssistant-0.1.2-macos-arm64.pkg -target /
# 或解压 .app.zip 后拖入 /Applications
```

首次运行需授予屏幕录制、辅助功能、麦克风。

**Linux**

```bash
sudo apt install ./aiassistant_0.1.2_amd64.deb
# 或
tar -xzf aiassistant-0.1.2-linux-x64.tar.gz
cd aiassistant-0.1.2-linux-x64 && ./aiassistant
```

便携包含顶层目录，不会向当前目录散落文件；启动器/二进制使用相对路径寻址。

## 6. 升级、回滚与卸载

| 平台 | 升级 | 回滚 | 卸载 |
|---|---|---|---|
| Windows | 新 MSI 覆盖（MajorUpgrade） | 卸载后装旧 MSI | 应用与功能；数据默认保留 |
| macOS | 新 pkg/app 替换 | 恢复旧 app 或 pkg | 删除 `/Applications/AiAssistant.app` |
| Linux | 新 deb/tar 覆盖 | 重装旧 deb 或解压旧 tar | `apt remove aiassistant`；数据目录保留 |

升级前执行 `--backup` 或复制数据目录。若已升级 Schema，回滚后需用对应版本备份恢复。卸载不会自动删除用户数据；需要彻底清除时先执行 `--purge-data --confirm-purge`。

## 7. 发布校验清单

- [ ] 三平台 build/format/test/audit/SBOM 全绿；源码树无逃逸产物。
- [ ] macOS `build/native/mac-helper.sh test` 与 `smoke` 通过。
- [ ] Windows portable 与 MSI 来自同一 `publish/win-x64`；签名验证通过。
- [ ] macOS Helper 位于 `.app/Contents/MacOS/`；签名/公证/装订通过。
- [ ] Linux tar 顶层目录正确；deb 版本/架构/依赖正确。
- [ ] `AiAssistant-<ver>-SHA256SUMS.txt` 覆盖全部分发包；SBOM 校验通过。
- [ ] 安装、升级、回滚、卸载在干净环境各验证一次。

## 8. 已知限制

- macOS 仅 arm64 正式交付；Intel 需另出 `osx-x64`/universal。
- Windows 未签名开发包会被 SmartScreen 拦截；正式版依赖 CA 证书。
- Linux 无托盘环境关闭窗口即退出；Wayland 抓屏依赖 portal 用户批准。
- 性能与资源目标（常驻 `<400 MB`、采集队列有界、媒体 30 天保留）由测试与浸泡验证；结果写入 `artifacts/test-results/`，不在此手工抄录。
