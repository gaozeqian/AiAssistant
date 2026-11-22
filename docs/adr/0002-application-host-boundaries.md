# ADR-0002：物理分层与单向依赖约束（Core / Application / Host 分离）

- **状态**：已采纳 (Accepted)
- **日期**：2026-11-20
- **阶段**：S0

## 背景

`DESIGN.md` 给出了逻辑模块划分，工程落地时需要明确接口放置位置与依赖组装位置，避免 `Desktop` 直接引用 `Storage` 或 `Platform.*`，也避免 `Core` 被第三方库污染。

## 决策

建立以下严格单向依赖规则，并由 `AiAssistant.Architecture.Tests` 在每次构建测试中同时检查编译程序集与 `.csproj` XML：

1. `AiAssistant.Core` 不引用任何内部项目，且不引用任何第三方 NuGet 包。
2. `AiAssistant.Application` 只引用 `AiAssistant.Core`（仅允许基础官方扩展抽象如 `Microsoft.Extensions.AI.Abstractions`），绝不引用 `Storage`、`Models`、`Platform.*`、`Desktop`。
3. `AiAssistant.Desktop` 只引用 `AiAssistant.Application` 与 `AiAssistant.Core`，绝不直接访问数据库、模型网关实现或平台 API。
4. `AiAssistant.Platform.Windows`、`AiAssistant.Platform.macOS`、`AiAssistant.Platform.Linux` 互不引用。
5. 只有 `AiAssistant.Host` 作为唯一 Composition Root 引用全部模块并完成依赖注入与宿主生命周期管理。

## 后果

- 任意平台采集器、存储后端或模型后端替换时，`Core`、`Application` 与 `Desktop` 零改动。
- 开发者在任意 OS（Windows / macOS / Linux）拉取仓库均可直接编译整套解决方案。
