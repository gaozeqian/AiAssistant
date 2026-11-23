# AX 驱动 UI 端到端测试（最接近真人）

`ax-e2e.swift` 通过 macOS 辅助功能（Accessibility / AXUIElement）驱动**打包后的真实 `.app`**：
枚举真实窗口控件、用真实鼠标事件（CGEvent）或 `AXPress` 点击、并断言可见状态变化。

## 前置条件（一次性授权，给运行本脚本的宿主进程）

1. 系统设置 → 隐私与安全性 → **辅助功能**：勾选运行本脚本的终端 / IDE。
2. 系统设置 → 隐私与安全性 → **屏幕与系统音频录制**：勾选同一进程（若做截图断言）。

检查是否已授权：

```bash
swift -e 'import ApplicationServices; print(AXIsProcessTrusted())'
```

## 用法

```bash
# 先打包（产生签名后的 .app，含 --show-window 钩子）
bash build/package/package-macos.sh --identity "<你的签名身份>" --version 0.1.0

# 打印窗口 AX 树（诊断控件角色/标题）
swift tools/ui-e2e/ax-e2e.swift probe

# 执行完整用户场景
swift tools/ui-e2e/ax-e2e.swift run
```

## 应用侧钩子

- `--show-window`：tray-only 应用默认不显示主窗口，该参数设置 `AIASSISTANT_UI_E2E_SHOW=1`，
  让 `ShellApp` 在启动时立即 `Show()` 主窗口，供 AX 驱动。
- `--data-directory <dir>`：隔离数据目录，避免污染真实数据。

两者都在 `src/AiAssistant.Host/Program.cs` 的 GUI 交接处映射为环境变量。

## 当前状态（2026-11-21）

已跑通（`swift ax-e2e.swift run` 5/5 通过）：

- 通过 AX 读取窗口标题、顶部横幅「正在本机采集」「语音休眠」；
- 通过 **Vision OCR 定位窗口截图中的文字 → 真实 CGEvent 坐标点击**，完成
  「切到隐私与控制 → 点击暂停采集 → 横幅变已暂停 → 点击恢复采集」的完整点击流程。

## 已知限制

Avalonia 的 macOS 辅助功能桥只暴露窗口、`TextBlock`（`kAXValue`）和 `TabGroup`，
**不暴露带名字的按钮/标签**（`AutomationProperties.Name` 未桥接到 NSAccessibility）。
因此点击走的是「截图 + OCR 定位 + 坐标点击」，而非按名点击。坐标点击依赖：

- 运行本脚本的宿主进程已授予 **屏幕与系统音频录制**（截图）与 **辅助功能**（CGEvent 点击）。

## 应用侧钩子

- `--show-window`：tray-only 应用默认不显示主窗口，该参数让 `ShellApp` 启动时立即显示主窗口。
- `--data-directory <dir>`：隔离数据目录，避免污染真实数据。
- `AIASSISTANT_THEME=light|dark`：固定主题（默认跟随系统）。

## 曾修复的阻塞 bug

窗口不显示的真因是**时区 bug**：`ReportsViewModel` / `DataManagementViewModel` 用
`new DateTimeOffset(DateTime.Today, TimeSpan.Zero)` 初始化日期，在非 UTC 机器（如 GMT+8）上
`DateTime.Today` 的 `Kind=Local` 与 `offset=Zero` 不匹配而抛异常，导致整个桌面外壳创建失败——
无窗口、无托盘，只有后台采集存活。已改为 `DateTimeOffset.Now.Date` 并补回归测试。

## 进程内替代（已可用）

`tests/AiAssistant.Desktop.Tests/DesktopViewModelTests.cs` 中的 `FullWindow_*` 场景在
Avalonia.Headless 内驱动真实 `MainWindow`（真实指针点击/键盘/文本输入），平台端口用 fake，
是当前稳定可跑的「带 UI」端到端自动化。

## 真实平台自驱动（Linux / 任意带显示环境，`--ui-e2e`）

Linux 的 GNOME Wayland 会拦截 XTest 外部输入注入，且 Avalonia 不向 AT-SPI 暴露控件树，
因此提供**进程内自驱动**的真实 UI 端到端模式：应用自己启动真实 Avalonia 窗口（原生
Wayland 或 X11 后端、真实 Skia 渲染、真实主题与托盘），在 UI 线程上按脚本切换 7 个页面并
执行暂停/恢复、语音启停、两步删除、诊断刷新、主题切换，每步用 `RenderTargetBitmap` 输出
PNG，最后写出通过/失败报告并退出。

```bash
AIASSISTANT_DATA_DIRECTORY=/tmp/aia-ui-e2e \
AIASSISTANT_MODELS_CONFIG=/path/to/models.json \
AIASSISTANT_SHERPA_MODEL_DIR=/path/to/sherpa \
  dotnet run --project tools/AiAssistant.Diagnostics.Host -c Release -- --ui-e2e
# 退出码 0 全部通过；1 有失败步骤；报告与截图写入 artifacts/test-results/e2e/ui-<时间>/
```

- 输出目录可用 `AIASSISTANT_UI_E2E_OUTPUT` 覆盖；语音模型/KWS 资产缺失时该步标记为跳过而非失败。
- 已知：真实桌面退出时异步服务释放可能阻塞，运行器写报告后会在 5 秒内确定性 `Environment.Exit`。
- 2026-11-22 在 Ubuntu 26.04 + GNOME Wayland 实测 11/11 通过（含真实窗口标题、暂停横幅、
  语音「Wake word ready」、诊断页 10 项能力、主题切换），截图见当次 `artifacts/test-results/e2e/` 目录。
