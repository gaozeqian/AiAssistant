# 路线图与当前状态

> **状态**：living ｜ **最后更新**：2026-11-27 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：阶段完成、优先级调整或发布计划变化时

本文件合并原 `status/status.md`、`status/backlog.md` 与 `release/known-limitations.md`。

## 1. 当前状态（2026-11-27）

- 版本 `0.1.8`；三平台交付矩阵可构建、可打包、可签名（正式凭据由发布环境提供）。
- 核心链路闭环：采集 → 加密存储 → 检索 → 日报/问答 → 导出；本地语音与只读 MCP 可用。
- 本阶段完成的全栈标准化重构：
  - 诊断/基准/UI E2E 物理迁入 `tools/AiAssistant.Diagnostics.Host`，产品程序集不再链接或排除它们。
  - `artifacts/` 统一为 `bin/obj/publish/staging/dist/cache/test-results/tmp`；SwiftPM 与测试临时目录全部收口。
  - CLI 命令化、`RuntimeServices` 类型化服务图、不可变 `DesktopLaunchContext`；`Program.cs` ≤150 行。
  - UI 按 7 个 Tab 拆分；诊断/时间线 ViewModel 无状态映射抽出，单文件 ≤350 行。
  - MCP 拆为传输/鉴权/生命周期；采集、语音、PipeWire、macOS 适配器与 Swift 能力按职责拆分。
  - 测试工程按模块拆为 15 个；新增 `Testing.Common`、Desktop/Host/Application/Models/LocalApi/Soak 工程；显式 Skip 替代静默 return。
  - 发布态二进制统一为 `AiAssistant` / `aiassistant`；安装包遵循各 OS 原生命名与目录规范。
  - 文档从 44 篇收敛为 25 篇，单一事实源与自动可达性检查。

## 2. 门禁基线

```bash
dotnet build AiAssistant.sln -c Release --warnaserror
dotnet format AiAssistant.sln --verify-no-changes
dotnet test --solution AiAssistant.sln -c Release
bash tools/audit-conventions.sh
python3 -B build/sbom/generate-sbom.py --verify --check-manifest
```

- 当前测试：300 例（295 通过 / 5 跳过），0 失败；受限环境用例显式 Skip（macOS 真机 Helper、真实浸泡、Linux portal）。
- 架构测试 13 项，审计 50+ 项；SBOM CycloneDX 1.6 校验通过。
- macOS Swift 构建/测试/`--smoke-check` 由 macOS CI 与本地 `build/native/mac-helper.sh` 验证。

## 3. 待办路线

### P0（发布前必须）

- [ ] 在 macOS CI 跑通 `MacHelperTests` 与真机 TCC 用例；保留失败诊断产物。
- [ ] 用正式 CA/Developer ID 凭据完成一次全链路签名、公证与安装验收。
- [ ] 在干净 Windows/macOS/Linux 环境验证安装、升级、回滚、卸载与数据保留。
- [ ] 为覆盖率设置基线阈值（先记录，再逐步提高），避免只上传不建门禁。

### P1（质量与可用性）

- [ ] Linux 真实 portal E2E 纳入受控 CI（需要可交互会话或模拟 portal）。
- [ ] macOS Intel/universal 包与签名评估。
- [ ] 扩大 ASR/KWS/OCR 黄金集与真实语料，建立跨版本回归基线。
- [ ] 大事件量（>100 万）下的检索/日报性能与内存曲线，补齐浸泡报告。
- [ ] Avalonia macOS 辅助功能桥增强，减少 OCR/坐标点击依赖。

### P2（能力扩展）

- [ ] 可选 Whisper/说话人分离资产的按需打包矩阵。
- [ ] MCP 工具级审计与更细粒度限流（保持只读与默认关闭）。
- [x] 跨设备加密备份/迁移：每个备份独立生成 256 位密钥（Crockford Base32），AES-256-GCM 分块加密数据库、媒体与主密钥；恢复时重新绑定目标机器的系统密钥库。备份密钥轮换仍待补充。

## 4. 非目标

- 不做云端账号、跨设备同步或远程管理面。
- 不做全盘监视、按键记录、剪贴板记录。
- 不引入微服务/多进程常驻架构；不为单一平台引入破坏性专属依赖。
