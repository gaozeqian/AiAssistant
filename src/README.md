# src/ — 产品代码

本目录只放产品源码与工程文件：16 个 .NET 工程，采用扁平布局（模块化单体 + 端口适配器，见 [ADR-0001](../docs/adr/0001-modular-monolith-and-hexagonal.md)）。

## 工程分层

| 工程 | 职责 |
|---|---|
| `AiAssistant.Core` | 领域模型与值对象，零第三方依赖 |
| `AiAssistant.Application` | 用例与端口定义 |
| `AiAssistant.Host` | 组合根、DI 注册、CLI 与生命周期 |
| `AiAssistant.Desktop` | Avalonia 桌面端（视图、ViewModel、导航） |
| `AiAssistant.Capture` / `Processing` / `Summarization` / `Scheduler` / `Storage` / `Models` / `Voice` / `Export` | 各业务能力实现 |
| `AiAssistant.LocalApi` | 只读本地 MCP 服务 |
| `AiAssistant.Platform.Windows` / `AiAssistant.Platform.macOS` / `AiAssistant.Platform.Linux` | 平台适配器，三者互不引用 |

## 约束

- 不放脚本、示例配置与文档；测试代码放 `tests/`。
- 依赖方向单向：`Core ← Application ← 实现 ← Host`，由 `tests/AiAssistant.Architecture.Tests` 自动校验。
- 包版本只在根 `Directory.Packages.props` 维护。
- 平台名称统一写 `macOS`（命名空间 `AiAssistant.Platform.macOS` 为 R3 明确例外）。
