# 文档拆分映射（2026-11-22）

> **状态**：archived ｜ **最后更新**：2026-11-22 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：不再维护

`PROGRESS.md`（原 465 行）拆分为四篇，原文整份存于 [../progress-2026-11-22.md](../progress-2026-11-22.md)。以下为原文段落 → 新位置的映射，可据此抽查信息无丢失。

| 原文段落 | 行号 | 新位置 |
|---|---|---|
| 标题与用途说明 | 1–7 | 分散到各新文件头 |
| §1 当前总体状态快照 | 8–18 | `docs/status/status.md` |
| §2 阶段门禁总表 | 19–35 | `docs/status/status.md` |
| §3 跨电脑/跨平台迁移接续指南 | 36–325 | `docs/guides/operations.md` |
| §3.1–3.3 环境/自检/启动 | 38–60 | `docs/guides/operations.md` |
| §3.4 模型端点与日报 | 61–188 | `docs/guides/operations.md` |
| §3.5 本地语音模型与命令 | 189–228 | `docs/guides/operations.md` |
| §3.6 只读本地 MCP | 229–258 | `docs/guides/operations.md` |
| §3.7 维护命令 | 259–273 | `docs/guides/operations.md` |
| §3.8 发布打包 | 274–293 | `docs/guides/operations.md` |
| §3.9 多日加速浸泡 | 294–306 | `docs/guides/operations.md` |
| §3.10 Wayland 连续抓屏 | 307–325 | `docs/guides/operations.md` |
| §4 实施日志 | 326–420 | `docs/status/log.md`（时间倒序） |
| §5 遗留项与门禁豁免 | 421–449 | `docs/status/backlog.md` |
| §6 未完成项执行顺序 | 450–465 | `docs/status/backlog.md` |

## 无损核对

- §4 日志条目数：拆分前后均为 **90** 条。
- 其余段落逐段搬移，仅做标题与交叉引用改写，未删减正文。
- 已知改写：§2 中「Windows zip+MSIX 模板」按既定决策改为「Windows zip+MSI」。

## 同批文档改名

| 旧 | 新 |
|---|---|
| `docs/MODELS.md` | `docs/guides/models.md` |
| `docs/compliance/DEPENDENCY_MANIFEST.md` | `docs/compliance/dependency-manifest.md` |
| `docs/compliance/MODEL-ASSETS.md` | `docs/compliance/model-assets.md` |
| `docs/release/*.md`（UPPERCASE） | `docs/release/*.md`（kebab-case） |
