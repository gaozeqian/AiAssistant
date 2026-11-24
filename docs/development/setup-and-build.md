# 环境搭建、构建与调试

> **状态**：living ｜ **最后更新**：2026-11-23 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：SDK、构建入口、脚本或平台前置变化时

## 1. 先决条件

| 工具 | 版本 | 用途 |
|---|---|---|
| .NET SDK | 10.0.x（`global.json` 固定，允许 11+ 前滚） | 全部 .NET 工程 |
| Python | 3.10+ | SBOM、deb 兜底、审计脚本 |
| Swift / Xcode CLT | Swift 6.0+，仅 macOS | `native/AiAssistant.MacHelper` |
| WiX | `dotnet tool restore` 后可用 | Windows MSI |
| bash / Git | POSIX shell | 脚本与仓库根解析 |

平台运行期依赖：Windows 无额外 VC++ 运行库需求；macOS 13+；Linux 需要 `libsecret-1-0`、`libx11-6`、`libice6`、`libsm6`、`libfontconfig1`，Wayland 抓屏还需 `xdg-desktop-portal` + `pipewire`。

## 2. 首次构建

```bash
dotnet restore AiAssistant.sln
dotnet build AiAssistant.sln -c Release --warnaserror
dotnet test --solution AiAssistant.sln -c Release
```

所有编译产物位于 `artifacts/bin/` 与 `artifacts/obj/`；源码树不得出现 `bin/`、`obj/`、`.build/`。

## 3. 启动与调试

```bash
# 桌面端（真实 Avalonia/AppKit/GTK）
dotnet run --project src/AiAssistant.Host/AiAssistant.Host.csproj

# 无头自检
dotnet run --project src/AiAssistant.Host/AiAssistant.Host.csproj -- --doctor
dotnet run --project src/AiAssistant.Host/AiAssistant.Host.csproj -- --startup-check

# 隔离数据目录
AIASSISTANT_DATA_DIRECTORY=/tmp/aia-dev \
  dotnet run --project src/AiAssistant.Host/AiAssistant.Host.csproj
```

调试 UI 建议使用 Avalonia Headless 测试（`tests/AiAssistant.Desktop.Tests`）验证绑定、无障碍名与快捷键，再用真实窗口自驱动做端到端：

```bash
dotnet run --project tools/AiAssistant.Diagnostics.Host -c Release -- --ui-e2e
```

## 4. macOS Swift Helper

```bash
bash build/native/mac-helper.sh build   # release 构建
bash build/native/mac-helper.sh smoke   # --smoke-check
bash build/native/mac-helper.sh test    # XCTest: IPC/权限/音频
```

SwiftPM scratch 目录固定为 `artifacts/obj/native/mac-helper/`，不会在 `native/` 下生成 `.build/`。Helper 依赖 AppKit/AVFoundation，只能在 macOS 上构建与测试。

## 5. 质量门禁

```bash
dotnet build AiAssistant.sln -c Release --warnaserror
dotnet format AiAssistant.sln --verify-no-changes
dotnet test --solution AiAssistant.sln -c Release
bash tools/audit-conventions.sh
python3 -B build/sbom/generate-sbom.py --verify --check-manifest
git status --ignored --porcelain   # 确认源码树无逃逸产物
```

测试分层、Trait 过滤、黄金集与浸泡见 [测试与评测](testing-and-benchmarks.md)。

## 6. 常用打包

```bash
# Windows（先 portable，再 MSI 复用同一 publish 载荷）
pwsh build/package/package-windows.ps1 -SkipModelBundle -DevSelfSigned
pwsh build/package/package-windows-msi.ps1 -Version 0.1.1 -DevSelfSigned -CertificatePath <cer>

# macOS
bash build/package/package-macos.sh --skip-sign --skip-models

# Linux
bash build/package/package-linux.sh --skip-models
```

产物统一在 `artifacts/dist/<version>/`。完整命名与签名策略见 [发布与部署](../release/release-and-deployment.md)。

## 7. 排障

| 现象 | 处理 |
|---|---|
| `dotnet build` 出现源码树 `bin/obj` | 检查是否绕过根 `Directory.Build.props`；清理后重跑审计 |
| macOS 首次采集无事件 | 授予屏幕录制/辅助功能/麦克风；`--doctor` 查看 TCC 状态 |
| Linux Wayland 无法抓屏 | 批准 portal 对话框；`AIASSISTANT_LINUX_BACKEND=X11` 临时回退 |
| MSI 找不到载荷 | 先运行 `package-windows.ps1` 生成 `artifacts/publish/win-x64` |
| 测试因真机/模型被跳过 | 这是显式 `Assert.Skip`；按测试输出中的环境变量开启 |
| 文档审计断链 | 从 `docs/README.md` 建立链接，保证 ≤2 跳可达 |
