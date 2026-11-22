# ADR-0006：Linux 原生 Wayland 默认路径、X11/XWayland 回退与能力降级

- **状态**：已采纳 (Accepted)
- **日期**：2026-11-20
- **适用版本**：0.1.x
- **维护触发条件**：决策被取代或修订时新增 ADR，本文件结论不改
- **阶段**：S0 / S3

## 背景

`design.md` v2.5 确定 Linux 桌面 UI 采用 `Avalonia.Wayland` 原生 Wayland 作为默认路径，但 Avalonia 12.1 中 Wayland 后端仍带实验性标记，且 Wayland 安全模型限制应用直接抓取屏幕、枚举全局前台窗口或注册全局热键。

## 决策

1. `AiAssistant.Host` 在 Linux 启动时默认优先启用 `UseWayland()`；若探测到初始化异常、缺少 `WAYLAND_DISPLAY` 或用户显式配置 `AIASSISTANT_LINUX_BACKEND=X11`，自动回退至 X11/XWayland 并记录诊断事件。
2. 所有平台相关能力统一通过 `CapabilityState`（`Supported / PermissionRequired / Degraded / Unavailable / DisabledByPolicy`）向应用层与 UI 暴露：
   - Wayland 抓屏经 `xdg-desktop-portal` 授权，授权后优先消费 `OpenPipeWireRemote` 返回的连续帧流（私有一路 `pw_stream`、按最小采样间隔节流），失败时回退到 portal 按需截图；
   - 前台窗口与全局热键在不支持的合成器下报告 `Degraded` 并明确展示缺失项，绝不伪装成全功能。

## 后果

- 兼顾现代 Linux Wayland 桌面体验与 X11 兼容兜底，能力缺失对用户透明可诊断。
