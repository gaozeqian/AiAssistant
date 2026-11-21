# 依赖清单、许可证审计与 SBOM 流程

> 对应阶段：`S0 工程奠基与风险前置` 任务 6

## 1. 已批准第三方依赖与许可证矩阵

所有第三方 NuGet 依赖统一在根目录 `Directory.Packages.props` 中集中管控（CPM），禁止在子项目 `.csproj` 中私自指定版本号或引入 GPL/AGPL 等传染性许可证依赖。

| 包名 | 版本 | 所属项目边界 | 许可证 | 用途与原生依赖说明 |
|---|---|---|---|---|
| `Avalonia` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` / `Avalonia.Fonts.Inter` / `Avalonia.Headless` | `12.1.6` | `Desktop` / `Host` / `Platform.Tests` | MIT | 跨平台桌面 MVVM UI 与 Headless 测试宿主 |
| `Avalonia.Wayland` | `12.1.6` | `Host` | MIT | Linux 原生 Wayland 默认后端（实验性标注，支持自动回退 X11） |
| `Tmds.DBus.Protocol` | `0.95.2` | `Platform.Linux` | MIT | xdg-desktop-portal、AT-SPI2 会话总线协议与 Unix FD 传递；无额外原生托管捆绑依赖 |
| `CommunityToolkit.Mvvm` | `8.4.2` | `Desktop` | MIT | MVVM 源生成器与命令绑定 |
| `Microsoft.Data.Sqlite.Core` | `10.0.15` | `Storage` | MIT | ADO.NET SQLite 核心驱动（不含默认未加密 bundle） |
| `SQLite3MC.PCLRaw.bundle` | `2.7.1` | `Storage` | MIT | SQLite3 Multiple Ciphers 加密原生引擎（支持 `win-x64` / `osx-arm64` / `linux-x64`） |
| `HiraokaHyperTools.sqlite-vec` | `0.1.9` | `Storage` | MIT | `sqlite-vec` 向量检索扩展（由 `IVectorIndex` 隔离，含加密库内 SIMD 兜底实现） |
| `System.Security.Cryptography.ProtectedData` | `10.0.15` | `Storage` / `Platform.Windows` | MIT | Windows DPAPI 系统密钥保护 |
| `System.Numerics.Tensors` | `10.0.15` | `Storage` | MIT | 向量相似度 SIMD 兜底计算 |
| `Microsoft.Extensions.AI` / `Microsoft.Extensions.AI.Abstractions` | `10.11.0` | `Application` / `Models` | MIT | 标准 `IChatClient` / `IEmbeddingGenerator` 模型抽象 |
| `Microsoft.Extensions.DependencyInjection` / `Microsoft.Extensions.DependencyInjection.Abstractions` | `10.0.15` | `Host` | MIT | 组合根依赖注入 |
| `Microsoft.Extensions.Hosting` / `Microsoft.Extensions.Hosting.Abstractions` | `10.0.15` | `Host` | MIT | 宿主生命周期 |
| `Microsoft.Extensions.Logging.Abstractions` | `10.0.15` | 各业务模块 | MIT | 结构化日志抽象 |
| `Microsoft.Extensions.Options` | `10.0.15` | `Models` / `Scheduler` | MIT | 配置选项模式 |
| `org.k2fsa.sherpa.onnx` | `2.0.2` | `Processing` | Apache-2.0 | 离线中文 ASR、Silero VAD、pyannote 说话人分离；按 RID 携带原生 `sherpa-onnx` 运行库，模型资产不入库 |
| `Whisper.net` / `Whisper.net.Runtime` | `1.9.1` | `Processing` | MIT | 可选高质量批处理 ASR（`AIASSISTANT_ASR_ENGINE=whisper`）；运行时包携带各平台 ggml 原生库，发布时按 RID 裁剪，模型资产不入库 |
| `Microsoft.ML.OnnxRuntime.Managed` | `1.28.0` | `Processing` | MIT | RapidOCR 托管绑定；原生 ONNX Runtime 复用 `org.k2fsa.sherpa.onnx` 自带的 1.28.2，不额外增加原生库（ADR-0007） |
| `SkiaSharp` / `SkiaSharp.NativeAssets.Linux` | `3.119.4` | `Export` | MIT | PNG/PDF 栅格化；Linux 原生资产随导出库分发，保证 Linux 端渲染与测试可用 |
| `PDFsharp` | `6.1.1` | `Export` | MIT | 可检索 PDF、TrueType 字体子集嵌入 |
| `NAudio` | `3.3.0` | `Platform.Windows` | MIT | WASAPI 共享模式单路麦克风采集（仅 Windows 生效） |
| `NetArchTest.Rules` | `1.3.2` | `Architecture.Tests` | MIT | 编译产物与依赖方向自动化门禁 |
| `Microsoft.NET.Test.Sdk` | `17.14.1` | `tests/*` | MIT | 测试宿主 |
| `xunit` | `2.9.3` | `tests/*` | Apache-2.0 | 单元、集成与契约测试框架 |
| `xunit.runner.visualstudio` | `3.1.4` | `tests/*` | Apache-2.0 | 测试运行器 |
| `coverlet.collector` | `6.0.4` | `tests/*` | MIT | 覆盖率收集 |

**硬性约束**：`AiAssistant.Core` 保持**零第三方依赖**；许可证仅允许 `MIT`、`Apache-2.0`、`BSD-2-Clause`、`BSD-3-Clause`。

## 2. 本地生成 SBOM 与许可证扫描命令

首版使用**离线**生成器，直接读取 NuGet 还原图（`obj/project.assets.json`），无需安装外部工具，适合内网与低权限环境：

```bash
dotnet restore AiAssistant.sln

# 生成 CycloneDX 1.5 JSON SBOM（含 SHA-512 哈希与依赖关系）
python build/sbom/generate-sbom.py --verify

# 校验每个集中管理的包都在本清单中登记
python build/sbom/generate-sbom.py --check-manifest

# 可选：需要完整许可证文本时使用外部工具
dotnet tool install --global CycloneDX
dotnet CycloneDX AiAssistant.sln -o artifacts/sbom -fn bom-tool.json --json
dotnet tool install --global dotnet-project-licenses
dotnet-project-licenses -i AiAssistant.sln -u
```

`artifacts/sbom/bom.json` 会随 CI 上传；发布时同时生成 `SHA256SUMS` 供用户校验安装包。
