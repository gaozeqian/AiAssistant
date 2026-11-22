# ADR-0005：macOS 原生能力采用 Swift Helper 独立进程与版本化本地 IPC

- **状态**：已采纳 (Accepted)
- **日期**：2026-11-20
- **阶段**：S0 / S3

## 背景

macOS 的 ScreenCaptureKit、Vision、AVAudioEngine 与 Accessibility API 深度绑定 Swift 并发与 Objective-C 运行时，且受 TCC（屏幕录制、麦克风、辅助功能）权限状态影响大。在 .NET 进程内大规模手写 P/Invoke 易引发崩溃与权限声明混乱。

## 决策

1. 在 `native/AiAssistant.MacHelper` 中以原生 Swift 可执行程序封装 macOS 采集与系统 OCR/TTS 能力。
2. `.NET` 侧 `AiAssistant.Platform.macOS` 仅实现版本化 Unix Domain Socket IPC 客户端（含协议版本握手、一次性随机会话凭据鉴权、心跳与超时）。
3. 由监督器以指数退避策略管理 Helper 进程生命周期：Helper 崩溃或 TCC 权限被撤销时，对应采集源进入 `Degraded` / `PermissionRequired`，主界面与历史时间线正常可用。

## 后果

- 隔离原生崩溃风险，简化签名与公证边界。
