# 架构总览

> **状态**：living ｜ **最后更新**：2026-11-23 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：模块边界、依赖规则、运行时组合或安全边界变化时

本文件合并原 `design.md` 与 `implementation.md`，是架构层唯一事实源；具体决策依据见 [ADR 索引](../adr/README.md)。

## 1. 目标与约束

AiAssistant 是长期驻留桌面的个人活动追踪助手：本地采集屏幕/窗口/文件/音频，加密存储，生成日报与证据问答，并可选地暴露只读本地 MCP。核心约束：

- **默认仅本机**：数据、模型调用与导出都在本机完成；公网端点必须显式同意。
- **内存与磁盘有界**：常驻内存目标 `<400 MB`，采集队列与媒体保留有硬上限。
- **跨平台一致**：Windows / macOS / Linux 共享领域与用例，平台差异集中在适配器。
- **可审计**：依赖方向、环境变量、产物目录、测试工程与文档可达性都有自动门禁。

## 2. 模块化单体与六边形

```text
Desktop / Host / tools        ← 组合根与交付边界
        │
Platform.* / Storage / Models / Processing / Capture / Voice / Summarization
Scheduler / Export / LocalApi  ← 适配器与能力实现
        │
    Application                ← 用例 + 端口（IClock、IRepository、IModelGateway…）
        │
       Core                    ← 领域模型与值对象，零第三方依赖
```

替代方案（微服务、多进程常驻）会放大本地 IPC 鉴权面、跨进程事务与安装体积，故不采用。详见 [ADR-0001](../adr/0001-modular-monolith-and-hexagonal.md) 与 [ADR-0002](../adr/0002-application-host-boundaries.md)。

## 3. 物理模块

`src/` 为扁平布局，共 16 个工程：

| 工程 | 职责 |
|---|---|
| `AiAssistant.Core` | 领域模型、值对象、配置常量；零项目/包引用 |
| `AiAssistant.Application` | 用例与端口；只依赖 Core |
| `AiAssistant.Capture` | 有界采集调度、背压、去重、暂停语义 |
| `AiAssistant.Processing` | 预处理流水线、OCR、ASR、脱敏、说话人分离 |
| `AiAssistant.Storage` | SQLite3MC、迁移、媒体 AES-GCM、向量索引、保留与日志 |
| `AiAssistant.Models` | M.E.AI 网关、三档策略、密钥库、能力探测 |
| `AiAssistant.Summarization` | 活动分段、检索、证据问答、日报生成 |
| `AiAssistant.Scheduler` | 持久任务队列、补偿与单消费者 Worker |
| `AiAssistant.Voice` | 唤醒、命令解析、单路 AudioHub、TTS 反馈 |
| `AiAssistant.Export` | Markdown/SVG/PNG/PDF 导出与中文字体回退 |
| `AiAssistant.LocalApi` | 只读 Loopback MCP（HTTP/JSON-RPC/SSE） |
| `AiAssistant.Platform.Windows/macOS/Linux` | 平台采集、权限、自启与能力矩阵；三者互不引用 |
| `AiAssistant.Desktop` | Avalonia 12 视图、ViewModel、导航与主题 |
| `AiAssistant.Host` | 组合根、DI、CLI 命令路由与生命周期；唯一可引用全部模块 |

依赖矩阵由 `tests/AiAssistant.Architecture.Tests` 自动校验：Core 禁止项目/包依赖；Application 不得引用实现；平台工程不得互引或引用 Storage/Desktop/Host；Desktop 不引用平台适配器；只有 Host 可作为组合根引用全部模块。

## 4. 运行时组合

- `AppRuntime` 在 `CreateAsync` 中完成数据目录、密钥库、加密连接、模型配置、平台注册与 DI 构建。
- `RuntimeServices` 在首次访问时**一次性**解析 CLI 所需类型化服务；命令不再各自查询 `IServiceProvider`。
- `Program.Main` 只负责控制台编码、`--help/--version/--smoke-check`、运行时创建、单实例激活、CLI 路由与 Avalonia/AppKit 主线程交接。
- CLI 命令位于 `src/AiAssistant.Host/Commands/`，按历史优先级排列；`Program.cs` 保持 ≤150 行。
- `DesktopLaunchContext` 以不可变记录向 `ShellApp` 注入 ViewModel 工厂、托盘能力、单实例 socket 与可选 UI E2E 钩子，消除静态可变状态。
- `tools/AiAssistant.Diagnostics.Host` 编译基准与 UI 自驱动代码，产品程序集不引用这些实现。

## 5. 数据与安全边界

| 边界 | 机制 |
|---|---|
| 数据库 | SQLite3MC 加密；master key 由系统密钥库（DPAPI / Keychain / libsecret）保护 |
| 媒体 | AES-GCM；密钥只在选中预览或导出时解密，明文不落临时文件 |
| 平台 IPC | macOS 版本化 AF_UNIX JSON 帧；随机一次性 token；socket 位于用户私有目录（≤96 字节） |
| 本地 MCP | 仅 127.0.0.1；Host/Origin 白名单；Bearer 固定时间比较；协议版本校验；限流；四个只读工具 |
| 日志 | 结构化脱敏；禁止正文、密钥与完整路径；空 `catch` 必须走 `SuppressedExceptionLog` |
| 模型 | 三档策略与公网显式同意；loopback 端点绕过系统代理；密钥值只写不读 |

详见 [ADR-0003](../adr/0003-sqlite3mc-keystore-and-aesgcm-media.md)、[ADR-0005](../adr/0005-macos-swift-helper-ipc.md) 与 [安全与隐私](../release/security-and-privacy.md)。

## 6. 时间、任务与日报

- 统一时间模型：UTC 时刻 + 本地日期 + 时区（[ADR-0009](../adr/0009-utc-local-date-timezone-model.md)）；生产代码只通过 `IClock` 取时。
- 日报任务持久化、幂等，启动时恢复中断任务；日报按版本追加而非覆盖（[ADR-0010](../adr/0010-idempotent-jobs-and-report-versioning.md)）。
- 采集暂停会清空待处理队列，恢复后不补采暂停区间；重复帧按 pHash + 窗口键在时间窗内合并。

## 7. 跨平台策略

- Windows：Win32 采集、DPAPI、WinMM/WASAPI；自包含发布 + MSI。
- macOS：Swift Helper 承载 ScreenCaptureKit/AVFoundation/Accessibility/FSEvents，托管侧只经 IPC 调用；Developer ID 签名与公证。
- Linux：默认 Wayland（xdg-desktop-portal + PipeWire），失败回退 X11；libsecret、PulseAudio；无托盘环境关闭窗口即退出。
- 平台能力统一映射为 `CapabilityState`；缺失权限只降级相关能力，不使整条管线崩溃。

## 8. 产物与交付

构建、测试、打包与 SBOM 只写 `artifacts/`；发布态可执行文件统一为 `AiAssistant.exe` / `AiAssistant` / `aiassistant`，分发包命名遵循各 OS 原生规范。详见 [开发规范与产物](../development/conventions-and-artifacts.md)。
