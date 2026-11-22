# 发布与运维

> **状态**：living ｜ **最后更新**：2026-11-22 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：交付格式或发布流程变化时

| 文档 | 说明 | 状态 |
|---|---|---|
| [delivery-matrix.md](delivery-matrix.md) | 三平台交付矩阵 | living |
| [release-checklist.md](release-checklist.md) | 发布检查表 | living |
| [release-notes-template.md](release-notes-template.md) | 发布说明模板 | living |
| [release-notes-0.1.0.md](release-notes-0.1.0.md) | 0.1.0 交付说明 | snapshot |
| [upgrade-and-uninstall.md](upgrade-and-uninstall.md) | 升级、备份、恢复与卸载 | living |
| [rollback.md](rollback.md) | 回滚手册 | living |
| [known-limitations.md](known-limitations.md) | 已知限制 | living |
| [performance.md](performance.md) | 性能与资源报告 | snapshot |
| [privacy.md](privacy.md) | 隐私说明 | living |

Windows 只发 `zip` + `MSI`，不再构建 MSIX；macOS 发 `.pkg` + `.zip`；Linux 发 `tar.gz` + `.deb`。
