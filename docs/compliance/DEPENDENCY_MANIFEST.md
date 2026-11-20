# 依赖清单、许可证审计与 SBOM 流程

> 对应阶段：`S0 工程奠基与风险前置` 任务 6

## 1. 已批准第三方依赖与许可证矩阵

所有第三方 NuGet 依赖统一在根目录 `Directory.Packages.props` 中集中管控（CPM），禁止在子项目 `.csproj` 中私自指定版本号或引入 GPL/AGPL 等传染性许可证依赖。

| 包名 | 版本 | 所属项目边界 | 许可证 | 用途与原生依赖说明 |
|---|---|---|---|---|
| `Avalonia` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` / `Avalonia.Fonts.Inter` | `12.1.6` | `Desktop` / `Host` | MIT | 跨平台桌面 MVVM UI |
| `Avalonia.Wayland` | `12.1.6` | `Host` | MIT | Linux 原生 Wayland 默认后端（实验性标注，支持自动回退 X11） |
| `CommunityToolkit.Mvvm` | `8.4.2` | `Desktop` | MIT | MVVM 源生成器与命令绑定 |
| `Microsoft.Data.Sqlite.Core` | `10.0.15` | `Storage` | MIT | ADO.NET SQLite 核心驱动（不含默认未加密 bundle） |
| `SQLite3MC.PCLRaw.bundle` | `2.7.1` | `Storage` | MIT | SQLite3 Multiple Ciphers 加密原生引擎（支持 `win-x64` / `osx-arm64` / `linux-x64`） |
| `HiraokaHyperTools.sqlite-vec` | `0.1.9` | `Storage` | MIT | `sqlite-vec` 向量检索扩展（由 `IVectorIndex` 隔离，含加密库内 SIMD 兜底实现） |
| `System.Security.Cryptography.ProtectedData` | `10.0.15` | `Storage` / `Platform.Windows` | MIT | Windows DPAPI 系统密钥保护 |
| `Microsoft.Extensions.AI` / `Abstractions` | `10.11.0` | `Application` / `Models` | MIT | 标准 `IChatClient` / `IEmbeddingGenerator` 模型抽象 |
| `Microsoft.Extensions.Hosting` / `DI` / `Logging` | `10.0.15` | `Host` / 各业务模块 | MIT | 宿主生命周期、依赖注入与结构化日志 |
| `NetArchTest.Rules` | `1.3.2` | `Architecture.Tests` | MIT | 编译产物与依赖方向自动化门禁 |
| `xunit` / `Microsoft.NET.Test.Sdk` | `2.9.3` / `17.14.1` | `tests/*` | Apache-2.0 / MIT | 自动化测试套件 |

**硬性约束**：`AiAssistant.Core` 保持**零第三方依赖**；许可证仅允许 `MIT`、`Apache-2.0`、`BSD-2-Clause`、`BSD-3-Clause`。

## 2. 本地生成 SBOM 与许可证扫描命令

```bash
# 安装工具（首次）
dotnet tool install --global CycloneDX
dotnet tool install --global dotnet-project-licenses

# 生成 CycloneDX JSON SBOM
mkdir -p artifacts/sbom
dotnet CycloneDX AiAssistant.sln -o artifacts/sbom -fn bom.json --json

# 扫描全仓许可证
dotnet-project-licenses -i AiAssistant.sln -u
```
