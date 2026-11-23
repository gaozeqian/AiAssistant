# 合规、依赖与 SBOM

> **状态**：living ｜ **最后更新**：2026-11-23 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：依赖、许可证、模型资产或 SBOM 流程变化时

本文件合并原 `compliance/dependency-manifest.md`、`compliance/model-assets.md` 与发布合规说明。

## 1. 依赖原则

- 仅使用宽松开源许可证（MIT / Apache-2.0 / BSD-2/3-Clause）。
- 包版本集中在 `Directory.Packages.props`；生产代码不新增未登记依赖。
- NuGet 审计模式 `all`、级别 `low`；`--warnaserror` 阻止已知漏洞与警告进入构建。
- 原生依赖只保留当前 RID；ONNX Runtime 由 sherpa-onnx 提供，禁止另引 `Microsoft.ML.OnnxRuntime` 覆盖 SONAME。

## 2. 完整依赖与许可证清单

中央包管理清单（`Directory.Packages.props`）中的每个包都必须在此出现。

| 包 | 版本 | 许可证 | 用途 |
|---|---|---|---|
| `Avalonia` | 12.1.6 | MIT | 桌面 UI |
| `Avalonia.Desktop` | 12.1.6 | MIT | 桌面平台后端 |
| `Avalonia.Themes.Fluent` | 12.1.6 | MIT | Fluent 主题 |
| `Avalonia.Fonts.Inter` | 12.1.6 | MIT | Inter 字体 |
| `Avalonia.Headless` | 12.1.6 | MIT | Headless 测试 |
| `Avalonia.Wayland` | 12.1.6 | MIT | Wayland 后端 |
| `Tmds.DBus.Protocol` | 0.95.2 | MIT | Linux portal/AT-SPI |
| `CommunityToolkit.Mvvm` | 8.4.2 | MIT | MVVM |
| `SkiaSharp` | 3.119.4 | MIT | PNG/PDF 光栅化 |
| `SkiaSharp.NativeAssets.Linux` | 3.119.4 | MIT | Linux 原生资产 |
| `PDFsharp` | 6.1.1 | MIT | PDF 导出 |
| `Microsoft.Data.Sqlite.Core` | 10.0.15 | MIT | SQLite 访问 |
| `SQLite3MC.PCLRaw.bundle` | 2.7.1 | MIT | SQLite 加密 |
| `HiraokaHyperTools.sqlite-vec` | 0.1.9 | MIT | 向量检索 |
| `System.Security.Cryptography.ProtectedData` | 10.0.15 | MIT | Windows DPAPI |
| `Microsoft.Extensions.AI` | 10.11.0 | MIT | AI 抽象 |
| `Microsoft.Extensions.AI.Abstractions` | 10.11.0 | MIT | AI 抽象 |
| `Microsoft.Extensions.DependencyInjection` | 10.0.15 | MIT | DI |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.15 | MIT | DI 抽象 |
| `Microsoft.Extensions.Hosting` | 10.0.15 | MIT | 宿主生命周期 |
| `Microsoft.Extensions.Hosting.Abstractions` | 10.0.15 | MIT | 宿主抽象 |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.15 | MIT | 日志抽象 |
| `System.Numerics.Tensors` | 10.0.15 | MIT | 向量运算 |
| `org.k2fsa.sherpa.onnx` | 2.0.2 | Apache-2.0 | 语音识别/唤醒/TTS |
| `Whisper.net` | 1.9.1 | MIT | 可选 ASR |
| `Whisper.net.Runtime` | 1.9.1 | MIT | Whisper 原生运行时 |
| `NAudio` | 3.3.0 | MIT | Windows 音频 |
| `Microsoft.ML.OnnxRuntime.Managed` | 1.28.0 | MIT | 托管绑定 |
| `xunit.v3` | 3.2.2 | MIT | 测试框架 |
| `Microsoft.Testing.Extensions.CodeCoverage` | 17.14.2 | MIT | 覆盖率 |
| `NetArchTest.Rules` | 1.3.2 | MIT | 架构测试 |

Swift Helper 仅使用 Apple 系统框架（AppKit、AVFoundation、ScreenCaptureKit、Vision、CoreServices），无第三方 Swift 包。

## 3. 随包模型资产

| 资产 | 许可证 | 说明 |
|---|---|---|
| sherpa-onnx KWS（wenetspeech 3.3M） | Apache-2.0 | 唤醒词「你好时光」 |
| sherpa-onnx Paraformer zh small int8 | Apache-2.0 | 中文命令 ASR |
| MeloTTS zh_en | MIT | 语音反馈；含 jieba 词典与 FST |
| Silero VAD v5 | MIT | 语音活动检测 |

模型权重不入库；`build/models/fetch-bundle.sh` 下载到 `artifacts/cache/models/`，`stage-bundle.sh` 汇总为 `bundle/`。已移除许可不明的 AISHELL-3 VITS；退役资产保留在 `artifacts/test-results/retired-models/`，不参与打包。

## 4. SBOM

```bash
dotnet restore AiAssistant.sln
python3 -B build/sbom/generate-sbom.py --verify --check-manifest
```

- 输出 `artifacts/dist/<version>/AiAssistant-<version>-sbom.cdx.json`（CycloneDX 1.6）。
- 读取 `artifacts/obj/**/project.assets.json` 的直接 + 传递依赖，离线生成。
- 校验结构、许可证白名单与 CPM 覆盖；组件包含应用、NuGet 库、Swift Helper 与模型 ML-BOM。
- CI 在 `sbom-and-licenses` 作业执行同一命令，并随 Release 分发。

## 5. 法务文件

`LICENSE`、`EULA.md`、`THIRD-PARTY-NOTICES.md` 与 ONNX Runtime 声明随四个平台安装包分发（`build/package/legal-files.*`，缺失即打包失败）。第三方组件归属以 `THIRD-PARTY-NOTICES.md` 与 SBOM 为准。

## 6. 审计

`tools/audit-conventions.sh` 检查：许可证文件存在、四个打包脚本接入法务文件、无重复二进制资产、依赖文档覆盖、SBOM 可验证。发布前必须 0 项不合规。
