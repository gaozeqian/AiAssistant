# tests/ — 测试代码与测试资产

本目录只放测试工程与测试资产，不放产品源码、原生工程或构建脚本。所有临时文件写入
`artifacts/tmp/<suite>/<guid>`；源码树不得出现 `bin/`、`obj/`、`test-temp/` 或嵌套 `artifacts/`。

## 工程矩阵

| 工程 | 覆盖 | 默认 Category |
|---|---|---|
| `AiAssistant.Testing.Common` | 共享基建：`FakeClock`、`TempArtifactDirectory`、`ProcessEnvironmentScope`、`AsyncAssert`（非测试程序集） | — |
| `AiAssistant.Architecture.Tests` | 物理分层、依赖方向、测试工程登记与静默 `return` 门禁（NetArchTest） | Unit |
| `AiAssistant.Core.Tests` | 领域模型、值对象与核心策略 | Unit |
| `AiAssistant.Application.Tests` | UseCase、策略、SVG/PNG 编解码 | Unit |
| `AiAssistant.Storage.Tests` | SQLite3MC 加密库、迁移、检索、日报持久化、保留与日志 | Unit / Integration |
| `AiAssistant.Capture.Tests` | 采集调度、背压、去重与暂停语义 | Unit |
| `AiAssistant.Processing.Tests` | 预处理、OCR、ASR、评测基准 | Unit |
| `AiAssistant.Voice.Tests` | 唤醒、命令解析、音频管线 | Unit |
| `AiAssistant.Models.Tests` | 模型网关、路由策略、端点代理、密钥管理、能力探测 | Unit / Integration |
| `AiAssistant.LocalApi.Tests` | Loopback MCP JSON-RPC、鉴权、限流、传输硬化 | Integration |
| `AiAssistant.Report.Tests` | Summarization、Export、Scheduler 报告链路 | Unit / Integration |
| `AiAssistant.Desktop.Tests` | Avalonia Headless UI、ViewModel、无障碍名与快捷键（不引用任何 `Platform.*`） | UI |
| `AiAssistant.Platform.Tests` | 三平台契约与适配器，按 `Windows/`、`macOS/`、`Linux/` 分目录 | Integration / LiveHardware |
| `AiAssistant.Soak.Tests` | 加速与真实时钟浸泡（受环境变量控制，缺省显式 Skip） | Soak |
| `AiAssistant.Host.Tests` | DI 全模块解析、CLI 参数解析、`0/1/2/3` 退出码契约 | Unit / Integration |

## 分层标签

```bash
# 毫秒级日常回归（默认）
dotnet test --solution AiAssistant.sln -c Release \
  --filter-trait "Category=Unit"

# 平台契约 / 集成
dotnet test --solution AiAssistant.sln -c Release \
  --filter-trait "Category=Integration"

# UI 与浸泡
dotnet test --solution AiAssistant.sln -c Release --filter-trait "Category=UI" --filter-trait "Category=Soak"

# 真机硬件
AIASSISTANT_MAC_HELPER_E2E=1 dotnet test --solution AiAssistant.sln -c Release \
  --filter-trait "Category=LiveHardware"
```

平台契约可单独用 `--filter-trait "OS=Windows"`（同理 `OS=macOS` / `OS=Linux`）过滤。所有 `[Trait]` 均由源码显式声明。

## 约束

- 测试只写 `artifacts/tmp/`（通过 `TempArtifactDirectory`），结束自清理；不得写死仓库相对路径或 `/tmp`。
- 需要外部模型、真机或较长耗时的用例必须用 xunit.v3 `Assert.Skip` / `Assert.SkipUnless` 显式跳过并说明原因，**禁止静默 `return`**（由 `DependencyRuleTests` 与审计脚本双重拦截）。
- 修改进程环境变量的测试必须加入 `[Collection("ProcessEnvironment")]` 并用 `ProcessEnvironmentScope` 恢复。
- 新增测试工程需同步 `AiAssistant.sln` 与本表；测试工程拓扑由架构测试校验。

## 运行

```bash
dotnet test --solution AiAssistant.sln -c Release
dotnet test tests/AiAssistant.Architecture.Tests/AiAssistant.Architecture.Tests.csproj -c Release
```
