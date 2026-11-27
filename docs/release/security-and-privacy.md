# 安全与隐私

> **状态**：living ｜ **最后更新**：2026-11-23 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：威胁模型、加密、权限、日志或数据保留策略变化时

本文件合并原 `release/privacy.md`、`release/known-limitations.md` 与部分 `release/performance.md`；漏洞报告流程见 [SECURITY.md](../../SECURITY.md)。

## 1. 信任边界

| 边界 | 信任假设 |
|---|---|
| 本机用户会话 | 可信；同一用户下的其他进程不在防护范围内 |
| 屏幕/窗口/文件/音频 | 只读采集；经用户权限授予 |
| SQLite3MC 数据库与媒体 | 静态加密；密钥由系统密钥库保护 |
| macOS Helper | 同用户本地进程；版本化 AF_UNIX IPC + 一次性 token |
| 本地 MCP | 仅 loopback；Bearer 令牌；Origin/Host 白名单；限流 |
| 模型端点 | 本机默认；内网/公网需策略允许，公网需显式同意 |

## 2. 静态加密

- **数据库**：SQLite3MC 加密；master key 由 DPAPI（Windows）、Keychain（macOS）或 libsecret（Linux）保护。
- **媒体**：AES-GCM；密钥只在选中预览、导出或语音反馈时解密；明文不写临时文件，使用后清零。
- **模型密钥**：AES-GCM 加密库；`models.json` 只保存 `apiKeySecret` 名称；密钥值只写不读。
- **备份**：可移植加密备份（数据库 + 媒体 + 主密钥），整体由每个备份独立生成的 256 位密钥经 AES-256-GCM 分块加密；密钥只在导出时显示、不写入应用，丢失后无法恢复。恢复需 `--backup-key` 与 `--confirm-restore`；主密钥会重新绑定目标机器的系统密钥库，因此可跨机器、跨系统用户恢复。

Schema 迁移与密钥库设计见 [ADR-0003](../adr/0003-sqlite3mc-keystore-and-aesgcm-media.md)。

## 3. 本地 IPC 与 MCP

**macOS Helper**

- 版本化握手；协议不兼容进入终态而不是重启循环。
- 一次性 32 字节 token，固定时间比较。
- socket 位于用户私有目录（macOS `$TMPDIR`，Linux `$XDG_RUNTIME_DIR/aiassistant`），`0700`，`sun_path ≤96` 字节；不回退共享 `/tmp`。

**Loopback MCP**

- 仅绑定 `127.0.0.1`；拒绝非 loopback peer。
- Host/Origin 白名单防 DNS rebinding；未授权请求不消耗限流配额。
- Bearer 固定时间比较；`MCP-Protocol-Version` 协商；默认关闭。
- 只暴露四个只读工具，无写工具。

## 4. 模型调用与数据外发

- 默认 `LocalOffline`；内网/公网端点由 `models.json` 中的端点档位约束——选择公网档位即视为在 UI 中显式同意外发，未配置公网端点时云端调用被策略拒绝。
- loopback 端点显式绕过系统代理，避免请求被代理转发。
- 发送前脱敏（`ITextRedactor`），模型审计只记录元数据：用途、档位、模型、长度、耗时、错误类型。
- 不存在“无证据也生成答案”的路径；证据缺失时明确降级。

## 5. 日志、导出与诊断

- 结构化日志只含元数据；禁止正文、密钥、完整文件路径。
- 空 `catch` 必须经 `SuppressedExceptionLog.Trace` 脱敏记录。
- 诊断导出先生成脱敏预览，用户确认后才写盘。
- 报告与问答导出在本机生成；媒体清理后只保留结构化证据。

## 6. 采集范围与隐私默认

- 只采集明确白名单目录的文件活动；默认不监视全盘、不记录按键或剪贴板。
- 应用/窗口黑名单内置密码管理器与隐私浏览规则；用户可追加关键字。
- 暂停会立即阻断入口并清空待处理队列；恢复后**不补采**暂停期间内容。
- 媒体默认保留 30 天；命令音频默认不落盘，开启后保留 7 天。保留天数与存储上限可在「数据与导出」页调整，超限时从最旧媒体开始擦除。
- 退出应用前可选择清理数据；`--purge-data --confirm-purge` 删除结构化事件、媒体、日报、任务与语音日志。

## 7. 平台权限

| 平台 | 权限 |
|---|---|
| Windows | 麦克风（桌面应用）；无需全盘/管理员权限 |
| macOS | 屏幕录制、辅助功能、麦克风（TCC）；Helper 随 `.app` 分发 |
| Linux | portal 屏幕共享批准；麦克风；libsecret 钥匙环 |

缺失权限只降级相关能力（`CapabilityState`），不使整条管线崩溃。

## 8. 已知限制

- 同一用户下的恶意本地进程不在防护范围；MCP 仍以 loopback + token 降低风险。
- macOS Avalonia 辅助功能桥不暴露带名字的按钮，AX 驱动部分场景需 OCR/坐标点击。
- Linux GNOME 默认无托盘；关闭窗口即退出。Wayland 外部输入注入可能被合成器拦截。
- 完整磁盘加密、终端防截屏、内核级 EDR 不在应用职责内。
- 公网模型的数据处理受端点提供方条款约束；应用只在用户显式同意后发送脱敏文本。
