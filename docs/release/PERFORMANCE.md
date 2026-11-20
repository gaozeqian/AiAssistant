# 性能与资源报告（首版）

> 测量环境：Windows 11 (`win-x64`, OS 10.0.26300)，.NET SDK 11.0.100，目标框架 `net10.0`。
> macOS / Linux 数据在真机复测前沿用同一构建配置，未做跨平台性能推断。

## 1. 构建与测试

| 指标 | 数值 | 说明 |
|---|---|---|
| Release 全量构建 | 0 警告 / 0 错误 | `dotnet build AiAssistant.sln -c Release` |
| 测试总数 | 113 | 8 个测试项目（含 3 天加速浸泡） |
| 测试总耗时 | 约 17 秒 | 各项目并行；Platform 17s（其中 3 天浸泡 15.8s）、Storage 5s、Report 0.9s |
| 架构门禁 | 6 项 | 依赖方向与合成违规检测 |

## 2. 启动

| 指标 | 数值 | 方法 |
|---|---|---|
| 冷启动自检 | **0.82 s** | 自包含 `win-x64`，全新数据目录，`--startup-check`（含加密库创建、Schema 迁移、任务恢复） |
| 二次启动自检 | 约 0.5–0.8 s | 已有加密库，无迁移 |
| 启动失败退出码 | 2 | 捕获异常并输出可诊断错误，不产生半成品数据库 |

## 3. 安装包体积

| 平台 | 产物 | 体积 |
|---|---|---|
| Windows | 自包含目录（staging） | 239 MB |
| Windows | `AiAssistant-<ver>-win-x64.zip` | **83 MB** |
| Linux | 自包含目录（`usr/lib/aiassistant`） | 144 MB |
| Linux | `.tar.gz` | **58 MB** |
| macOS | `AiAssistant.app`（含 .NET 运行时） | 155 MB |

体积主要来自 .NET 自包含运行时与 sherpa-onnx 原生库；如需更小安装包可改用框架依赖发布（`-FrameworkDependent` / `--framework-dependent`），代价是要求目标机安装 .NET 10 运行时。

## 4. 存储与检索

| 场景 | 观测 |
|---|---|
| 加密数据库初始化（新建） | 包含在 0.82 s 启动自检内 |
| 16 项存储/迁移/删除测试 | 3 s（含多次新建加密库、媒体写入、备份恢复） |
| 24h 加速浸泡（S2） | 48/48 控制事件零丢失，队列严格受限，日志零正文泄露 |
| **3 天加速浸泡（S9）** | **15.8 s** 完成 72 小时模拟：146/146 控制事件零丢失、144 条黑名单拦截、队列峰值 12/12、289 条事件、72 个媒体、8 小时休眠窗口零事件、完整性 `ok`、保留清理 72/72 |
| 向量检索 | `sqlite-vec` 原生加载；失败时同接口 SIMD 兜底，不降低加密要求 |

多日浸泡可用环境变量扩展到 14 天等效：

```bash
AIASSISTANT_SOAK_DAYS=14 dotnet test tests/AiAssistant.Platform.Tests \
  -c Release --filter "FullyQualifiedName~MultiDayAcceleratedSoak"
```

报告写入 `artifacts/soak/soak-<days>d.md`，包含控制事件、黑名单、队列峰值、活跃日期、完整性、保留清理与泄露检查结论。

> 未做 10 万级事件压测与长稳内存曲线；`S9` 浸泡阶段应在目标机器补齐。

## 5. 语音与模型（未评测项）

- KWS/TTS、ASR 延迟与内存占用依赖外挂模型资产，未在本机评测（`S7-R1`）。
- 本机模型（Ollama Qwen3 4B–9B）首 token 与吞吐未评测（`S5-R1`）。
- 混合检索召回率与问答正确率未评测（`S8-R1`）。

## 6. 复现命令

```bash
dotnet build AiAssistant.sln -c Release
dotnet test AiAssistant.sln -c Release

# Windows 启动计时
AIASSISTANT_DATA_DIRECTORY=$(mktemp -d) time ./artifacts/packages/staging/win-x64/AiAssistant.Host.exe --startup-check

# 打包并观察体积
bash build/package/package-linux.sh
pwsh build/package/package-windows.ps1
```

## 7. 结论

- 启动路径满足「秒级可用」目标，加密初始化没有明显拖慢首屏。
- 自包含包体积偏大但可接受；企业分发可切换框架依赖模式。
- 性能门禁的剩余风险集中在长稳与模型推理，需在 S9 真机浸泡中补齐。
