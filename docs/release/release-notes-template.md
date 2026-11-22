# 发布说明模板

> **状态**：living ｜ **最后更新**：2026-11-22 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：发布说明结构或交付物清单变化时

> 复制本文件为 `docs/release/notes/v<version>.md`，按实际内容填写后附到 GitHub Release。

## AiAssistant <version>

发布日期：<yyyy-mm-dd>
提交：`<commit>`
产物：Windows zip / Windows MSI / macOS zip / Linux tar.gz / Linux deb / `bom.json` / `SHA256SUMS`

## 亮点

- 

## 修复

- 

## 变更与不兼容

- Schema 版本：v<old> → v<new>（向前迁移，已通过升级测试）
- 配置变更：
- 行为变更：

## 安装与校验

```bash
# 校验下载包
sha256sum -c SHA256SUMS
```

- Windows：解压 zip 后运行 `AiAssistant.Host.exe`；MSI 安装器会自动导入签名证书。
- macOS：解压后拖入「应用程序」，首次启动需在「隐私与安全性」允许。
- Linux：`tar -xzf AiAssistant-<version>-linux-x64.tar.gz -C /`（或 `sudo apt install ./AiAssistant-<version>-linux-x64.deb`）。

## 升级说明

1. 退出应用。
2. `AiAssistant.Host --backup <path>` 备份。
3. 安装新版本，首次启动自动迁移。
4. 抽查时间线、日报、检索与语音日志。

## 已知问题

- 见 `docs/release/known-limitations.md`，本次新增：

## 回滚

见 `docs/release/rollback.md`。保留上一版本安装包与升级前备份。

## 校验和

```
<粘贴 SHA256SUMS 内容>
```
