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
swift tests/ui-e2e/ax-e2e.swift probe

# 执行完整用户场景
swift tests/ui-e2e/ax-e2e.swift run
```

## 应用侧钩子

- `--show-window`：tray-only 应用默认不显示主窗口，该参数设置 `AIASSISTANT_UI_E2E_SHOW=1`，
  让 `ShellApp` 在启动时立即 `Show()` 主窗口，供 AX 驱动。
- `--data-directory <dir>`：隔离数据目录，避免污染真实数据。

两者都在 `src/AiAssistant.Host/Program.cs` 的 GUI 交接处映射为环境变量。

## 当前状态（2026-11-21）

**受阻**：在本机（macOS 26.7.3，Apple Silicon）上，无论 `open`、`open --args` 还是直接运行二进制，
应用的 Avalonia GUI 外壳 `OnFrameworkInitializationCompleted` 均**未执行**——既无窗口、也无托盘图标
（仅有后台采集进程与 Helper 存活，日志停留在 "Runtime initialized cleanly"）。`LogToTrace()` 无监听器，
Avalonia 初始化异常被静默吞掉，难以定位。

待办：给 Avalonia 配置一个 Trace 监听器（或改用 `LogToTrace(LogEventLevel.Error)` + 文件），
复现并定位 macOS GUI 初始化失败原因；或确认是否与 `runtime.StartAsync()` 在 Avalonia 之前、
于主线程执行有关。修复后，本驱动即可直接复用。

## 进程内替代（已可用）

`tests/AiAssistant.Platform.Tests/DesktopViewModelTests.cs` 中的 `FullWindow_*` 场景在
Avalonia.Headless 内驱动真实 `MainWindow`（真实指针点击/键盘/文本输入），平台端口用 fake，
是当前稳定可跑的「带 UI」端到端自动化。
