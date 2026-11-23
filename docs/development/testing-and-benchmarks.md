# 测试体系与评测

> **状态**：living ｜ **最后更新**：2026-11-23 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：测试工程、Trait、黄金集、评测入口或 CI 分层变化时

## 1. 测试金字塔与工程拓扑

| 层 | 工程 | 内容 |
|---|---|---|
| 单元 | `Core.Tests`、`Application.Tests`、`Capture.Tests`、`Processing.Tests`、`Voice.Tests`、`Storage.Tests`、`Models.Tests`、`Report.Tests` | 领域、用例、编解码、存储、模型、报告链 |
| 契约/集成 | `LocalApi.Tests`、`Platform.Tests`、`Host.Tests` | MCP 协议与鉴权、三平台契约、DI/CLI/退出码 |
| UI | `Desktop.Tests` | Avalonia Headless、ViewModel、无障碍名与快捷键；不引用任何 `Platform.*` |
| 浸泡 | `Soak.Tests` | 加速多日与真实时钟浸泡 |
| 架构 | `Architecture.Tests` | 依赖矩阵、内部暴露面、测试工程登记、静默 `return` 拦截 |
| 共享基建 | `Testing.Common` | `FakeClock`、`TempArtifactDirectory`、`ProcessEnvironmentScope`、`AsyncAssert`（非测试程序集） |

## 2. 分类与过滤

每个测试类声明 `[Trait("Category", ...)]`；平台用例额外声明 `[Trait("OS", ...)]`。

```bash
# 毫秒级日常回归
dotnet test --solution AiAssistant.sln -c Release --filter-trait "Category=Unit"

# 集成 / 平台契约
dotnet test --solution AiAssistant.sln -c Release --filter-trait "Category=Integration"

# UI 与浸泡
dotnet test --solution AiAssistant.sln -c Release --filter-trait "Category=UI" --filter-trait "Category=Soak"

# 真机硬件
AIASSISTANT_MAC_HELPER_E2E=1 dotnet test --solution AiAssistant.sln -c Release \
  --filter-trait "Category=LiveHardware"

# 平台叠加
dotnet test --solution AiAssistant.sln -c Release --filter-trait "OS=Linux"
```

受限环境用例必须使用 xunit.v3 `Assert.Skip` / `Assert.SkipUnless` 并给出原因；禁止 `if (...) return;`。架构测试与 `tools/audit-conventions.sh` 双重拦截。

## 3. 临时目录与环境变量

- 测试临时文件只写 `artifacts/tmp/<suite>/<guid>`（`TempArtifactDirectory` / `CreatePath`），结束自清理。
- 禁止 `AppContext.BaseDirectory/test-temp`、硬编码 `/tmp` 或 `Path.GetTempPath()`（Testing.Common 自身除外）。
- 修改进程环境变量的测试必须加入 `[Collection("ProcessEnvironment")]`，并使用 `ProcessEnvironmentScope` 恢复。
- `CA1001/CA2000` 不再全局压制；测试持有的连接、Socket 与流必须释放。

## 4. 覆盖率

CI 按 RID 运行完整测试并输出 Cobertura：

```bash
dotnet test --solution AiAssistant.sln -c Release --no-build \
  --coverage --coverage-output-format cobertura \
  --coverage-output artifacts/test-results/coverage/coverage.cobertura.xml
```

报告随 CI 产物上传；`tests/testconfig.json` 的结果目录固定为 `artifacts/test-results`。

## 5. 真机与浸泡开关

| 变量 | 作用 |
|---|---|
| `AIASSISTANT_SOAK_DAYS` | 多日加速浸泡天数（默认 3） |
| `AIASSISTANT_REAL_SOAK_MINUTES` | 真实时钟浸泡分钟数（1–120；未设置时显式 Skip） |
| `AIASSISTANT_MAC_HELPER_E2E` | macOS 真机 Swift Helper 用例 |
| `AIASSISTANT_PORTAL_E2E` | Linux 真实 portal/PipeWire 抓屏用例 |
| `AIASSISTANT_LINUX_BACKEND` | `wayland` / `x11` 强制后端 |
| `AIASSISTANT_WAYLAND_PIPEWIRE` | `false` 关闭 PipeWire 流 |

## 6. 黄金集与基准

开发专用宿主 `tools/AiAssistant.Diagnostics.Host` 不随产品发布：

```bash
# 检索（合成语料，真实 embedding 时需 Ollama）
dotnet run --project tools/AiAssistant.Diagnostics.Host -c Release -- \
  --benchmark-retrieval --output artifacts/test-results/benchmarks/retrieval.md

# ASR / KWS / OCR 黄金集
dotnet run --project tools/AiAssistant.Diagnostics.Host -c Release -- \
  --benchmark-asr --samples artifacts/cache/models/golden-sets/asr
dotnet run --project tools/AiAssistant.Diagnostics.Host -c Release -- \
  --benchmark-kws --samples artifacts/cache/models/golden-sets/kws
dotnet run --project tools/AiAssistant.Diagnostics.Host -c Release -- \
  --benchmark-ocr --samples artifacts/cache/models/golden-sets/ocr

# 真实窗口自驱动 E2E（截图/报告写入 artifacts/test-results/e2e）
dotnet run --project tools/AiAssistant.Diagnostics.Host -c Release -- --ui-e2e
```

样本格式：

- ASR：`<name>.wav` + 同名 `.txt`；任意采样率/声道自动转 16 kHz 单声道。
- OCR：`<name>.png` + 同名 `.txt`。
- KWS：`positive/` 与 `negative/` 两个子目录。
- 模型资产与真实语料不入库；下载与随包见 [模型指南](../guides/models.md)。

报告默认写入 `artifacts/test-results/benchmarks/`，包含 CER、唤醒率/误唤醒率/延迟、Recall@K/MRR 与逐样本诊断。模型缺失时报告标记 `engine unavailable` 并以非零退出码结束。

## 7. Swift Helper 测试

```bash
bash build/native/mac-helper.sh test
```

`native/AiAssistant.MacHelper/Tests/MacHelperTests` 覆盖 IPC 编解码、权限快照映射、音频参数与固定时间令牌比较；macOS CI 与本地共用同一入口。
