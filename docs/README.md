# 文档索引

> **状态**：living ｜ **最后更新**：2026-11-22 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：文档增删、目录调整或索引结构变化时

本文件是**唯一文档入口**：任何文档都应从本文件 ≤2 跳可达。新增文档请先挂到对应分类并附一行说明与状态。

## 入门

| 文档 | 说明 | 状态 |
|---|---|---|
| [../README.md](../README.md) | 仓库入口：定位、能力、5 分钟上手 | living |
| [guides/models.md](guides/models.md) | 本机模型配置与语音模型安装 | living |
| [guides/operations.md](guides/operations.md) | 迁移接续、维护命令、打包、浸泡、Wayland | living |

## 架构与设计

| 文档 | 说明 | 状态 |
|---|---|---|
| [architecture/design.md](architecture/design.md) | 为什么这么设计：方案、选型、跨平台、隐私 | living |
| [architecture/implementation.md](architecture/implementation.md) | 怎么做：实施步骤、物理分层、门禁、风险 | living |
| [adr/](adr/README.md) | 架构决策记录（0001–0010） | living |

## 开发指南

| 文档 | 说明 | 状态 |
|---|---|---|
| [guides/artifacts.md](guides/artifacts.md) | 产物分层、保留策略、清理与重建 | living |
| [guides/conventions.md](guides/conventions.md) | 目录/文档/脚本规范与豁免表 | living |
| [../CONTRIBUTING.md](../CONTRIBUTING.md) | 参与开发：环境、分支、提交、自检 | living |

## 参考

| 文档 | 说明 | 状态 |
|---|---|---|
| [reference/cli.md](reference/cli.md) | `AiAssistant.Host` 命令行参数权威清单 | living |
| [reference/environment-variables.md](reference/environment-variables.md) | 全部环境变量与默认值 | living |
| [reference/configuration.md](reference/configuration.md) | 配置键、默认模型与数据目录语义 | living |

## 测试与质量

| 文档 | 说明 | 状态 |
|---|---|---|
| [testing/README.md](testing/README.md) | 测试与质量索引（黄金集、基线） | living |
| [testing/golden-sets.md](testing/golden-sets.md) | ASR / OCR 识别黄金集与评测口径 | living |
| [testing/baselines/2026-11-21-macos-arm64.md](testing/baselines/2026-11-21-macos-arm64.md) | macOS arm64 识别基线 | snapshot |

## 发布与运维

| 文档 | 说明 | 状态 |
|---|---|---|
| [release/README.md](release/README.md) | 发布与运维索引 | living |
| [release/delivery-matrix.md](release/delivery-matrix.md) | 三平台交付矩阵 | living |
| [release/release-checklist.md](release/release-checklist.md) | 发布检查表 | living |
| [release/release-notes-template.md](release/release-notes-template.md) | 发布说明模板 | living |
| [release/release-notes-0.1.0.md](release/release-notes-0.1.0.md) | 0.1.0 交付说明 | snapshot |
| [release/upgrade-and-uninstall.md](release/upgrade-and-uninstall.md) | 升级、备份、恢复与卸载 | living |
| [release/rollback.md](release/rollback.md) | 回滚手册 | living |
| [release/known-limitations.md](release/known-limitations.md) | 已知限制 | living |
| [release/performance.md](release/performance.md) | 性能与资源报告 | snapshot |
| [release/privacy.md](release/privacy.md) | 隐私说明 | living |

## 合规

| 文档 | 说明 | 状态 |
|---|---|---|
| [compliance/README.md](compliance/README.md) | 合规索引（依赖、模型许可） | living |
| [compliance/dependency-manifest.md](compliance/dependency-manifest.md) | 依赖清单、许可证审计与 SBOM | living |
| [compliance/model-assets.md](compliance/model-assets.md) | 随包模型资产与许可证 | living |
| [../THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) | 第三方组件归属与许可证全文 | living |

## 状态

| 文档 | 说明 | 状态 |
|---|---|---|
| [status/status.md](status/status.md) | 当前状态与阶段门禁（唯一权威） | living |
| [status/log.md](status/log.md) | 实施日志（时间倒序） | living |
| [status/backlog.md](status/backlog.md) | 遗留项与执行顺序 | living |
| [status/conformance.md](status/conformance.md) | 规范合规矩阵（R1–R10） | living |

## 历史归档

| 文档 | 说明 | 状态 |
|---|---|---|
| [archive/progress-2026-11-22.md](archive/progress-2026-11-22.md) | 拆分前的 PROGRESS 原文 | archived |
| [archive/migrations/2026-11-22-repo-layout.md](archive/migrations/2026-11-22-repo-layout.md) | 仓库布局重构迁移映射 | archived |
| [archive/migrations/2026-11-22-docs-split.md](archive/migrations/2026-11-22-docs-split.md) | 文档拆分映射 | archived |
