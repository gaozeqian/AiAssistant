# 文档索引

> **状态**：living ｜ **最后更新**：2026-11-23 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：文档增删、目录调整或索引结构变化时

本文件是**唯一文档入口**：仓库内所有文档从本文件 ≤2 跳可达。新增文档请先挂到对应分类并附一行说明与状态。

## 入门

| 文档 | 说明 | 状态 |
|---|---|---|
| [../README.md](../README.md) | 仓库入口：定位、能力与 5 分钟上手 | living |
| [development/setup-and-build.md](development/setup-and-build.md) | 三平台环境搭建、构建、运行与调试 | living |
| [../CONTRIBUTING.md](../CONTRIBUTING.md) | 参与开发：分支、提交、自检与评审 | living |

## 架构

| 文档 | 说明 | 状态 |
|---|---|---|
| [architecture/overview.md](architecture/overview.md) | 模块化单体、六边形依赖、运行时组合、安全边界 | living |
| [adr/README.md](adr/README.md) | 架构决策记录索引（0001–0010） | living |
| [adr/0001-modular-monolith-and-hexagonal.md](adr/0001-modular-monolith-and-hexagonal.md) | 模块化单体 + 端口适配器 | accepted |
| [adr/0002-application-host-boundaries.md](adr/0002-application-host-boundaries.md) | Application 与 Host 边界 | accepted |
| [adr/0003-sqlite3mc-keystore-and-aesgcm-media.md](adr/0003-sqlite3mc-keystore-and-aesgcm-media.md) | SQLite3MC、系统密钥库与媒体 AES-GCM | accepted |
| [adr/0004-sqlite-vec-isolation-and-fallback.md](adr/0004-sqlite-vec-isolation-and-fallback.md) | sqlite-vec 隔离与回退 | accepted |
| [adr/0005-macos-swift-helper-ipc.md](adr/0005-macos-swift-helper-ipc.md) | macOS Swift Helper IPC | accepted |
| [adr/0006-linux-wayland-default-and-degradation.md](adr/0006-linux-wayland-default-and-degradation.md) | Linux Wayland 默认与降级 | accepted |
| [adr/0007-sherpa-onnx-and-single-audio-hub.md](adr/0007-sherpa-onnx-and-single-audio-hub.md) | sherpa-onnx 与单路 AudioHub | accepted |
| [adr/0008-meai-gateway-and-tier-decorators.md](adr/0008-meai-gateway-and-tier-decorators.md) | M.E.AI 网关与三档策略装饰器 | accepted |
| [adr/0009-utc-local-date-timezone-model.md](adr/0009-utc-local-date-timezone-model.md) | UTC + 本地日期 + 时区模型 | accepted |
| [adr/0010-idempotent-jobs-and-report-versioning.md](adr/0010-idempotent-jobs-and-report-versioning.md) | 幂等任务与日报版本 | accepted |

## 开发规范

| 文档 | 说明 | 状态 |
|---|---|---|
| [development/conventions-and-artifacts.md](development/conventions-and-artifacts.md) | 目录职责、零源码树污染、`artifacts/` 树与命名 | living |
| [development/testing-and-benchmarks.md](development/testing-and-benchmarks.md) | 测试金字塔、Trait 分层、黄金集与评测 | living |

## 参考

| 文档 | 说明 | 状态 |
|---|---|---|
| [reference/cli.md](reference/cli.md) | `AiAssistant` 命令行参数与退出码权威清单 | living |
| [reference/configuration.md](reference/configuration.md) | 数据目录、`models.json`、默认模型与端点策略 | living |
| [reference/environment-variables.md](reference/environment-variables.md) | 全部环境变量与默认值 | living |

## 指南

| 文档 | 说明 | 状态 |
|---|---|---|
| [guides/models.md](guides/models.md) | 本机模型配置、语音模型安装与 UI 联调 | living |
| [guides/operations.md](guides/operations.md) | 维护命令、备份恢复、MCP、日志与平台排障 | living |

## 安全、合规与发布

| 文档 | 说明 | 状态 |
|---|---|---|
| [release/security-and-privacy.md](release/security-and-privacy.md) | 威胁模型、加密、权限、隐私默认与限制 | living |
| [release/compliance.md](release/compliance.md) | 依赖与模型许可证、SBOM 与审计 | living |
| [release/release-and-deployment.md](release/release-and-deployment.md) | 交付矩阵、签名、安装升级回滚与发布流程 | living |
| [release/roadmap.md](release/roadmap.md) | 当前状态、已完成阶段与待办路线 | living |
| [../SECURITY.md](../SECURITY.md) | 漏洞报告流程 | living |
| [../CHANGELOG.md](../CHANGELOG.md) | 版本变更记录 | living |
| [../THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) | 第三方组件归属与许可证全文 | living |
