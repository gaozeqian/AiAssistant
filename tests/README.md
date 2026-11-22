# tests/ — 测试代码与测试资产

本目录只放测试工程与测试资产，不放产品源码、原生工程或构建脚本。

## 工程

| 工程 | 覆盖 |
|---|---|
| `AiAssistant.Architecture.Tests` | 物理分层与依赖方向门禁（NetArchTest）、仓库级约束 |
| `AiAssistant.Core.Tests` | 领域模型与值对象 |
| `AiAssistant.Storage.Tests` | 加密库、迁移、检索、日报持久化 |
| `AiAssistant.Capture.Tests` | 采集调度与去重 |
| `AiAssistant.Processing.Tests` | 预处理、OCR、ASR |
| `AiAssistant.Voice.Tests` | 唤醒、命令解析、音频管线 |
| `AiAssistant.Report.Tests` | 汇总、导出、模型网关、MCP 协议 |
| `AiAssistant.Platform.Tests` | 三平台契约、浸泡与真机（E2E）用例 |

## 约束

- 测试只写系统临时目录或 `artifacts/tmp/`，结束自清理；不得写死仓库相对路径。
- 需要外部模型或真机的用例必须显式跳过并说明原因（如 `AIASSISTANT_MAC_HELPER_E2E=1`），不得静默通过。
- 新增测试工程需同步 `AiAssistant.sln` 与本表。

## 运行

```bash
dotnet test -c Release --no-build
dotnet test tests/AiAssistant.Architecture.Tests/AiAssistant.Architecture.Tests.csproj -c Release
```
