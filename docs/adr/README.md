# 架构决策记录（ADR）

> **状态**：living ｜ **最后更新**：2026-11-22 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：新增 ADR 时

采用 [ADR](https://adr.github.io/) 记录重要架构决策。新决策请用 `NNNN-kebab-case.md`（四位数递增），并在此登记。

| 编号 | 决策 |
|---|---|
| [0001](0001-modular-monolith-and-hexagonal.md) | 模块化单体 + 端口适配器，而非过早微服务化 |
| [0002](0002-application-host-boundaries.md) | 物理分层与单向依赖（Core / Application / Host） |
| [0003](0003-sqlite3mc-keystore-and-aesgcm-media.md) | SQLite3MC 加密库、系统密钥库与媒体 AES-GCM |
| [0004](0004-sqlite-vec-isolation-and-fallback.md) | sqlite-vec 接口隔离、版本元数据与回退 |
| [0005](0005-macos-swift-helper-ipc.md) | macOS 原生能力走 Swift Helper + 版本化本地 IPC |
| [0006](0006-linux-wayland-default-and-degradation.md) | Linux 默认 Wayland、X11 回退与能力降级 |
| [0007](0007-sherpa-onnx-and-single-audio-hub.md) | sherpa-onnx 统一语音栈与单路 AudioHub |
| [0008](0008-meai-gateway-and-tier-decorators.md) | M.E.AI 统一网关与三档策略装饰器 |
| [0009](0009-utc-local-date-timezone-model.md) | UTC 时刻 + 本地日期 + 时区的混合时间模型 |
| [0010](0010-idempotent-jobs-and-report-versioning.md) | 任务持久幂等、启动补偿与版本化日报 |

旧决策**不修改**，用新 ADR 取代；被取代的决策在正文标注状态。
