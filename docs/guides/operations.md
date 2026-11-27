# 运维、维护与排障

> **状态**：living ｜ **最后更新**：2026-11-23 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：维护命令、备份恢复、MCP、升级卸载或平台前置变化时

本文件面向运行与维护；构建见 [环境搭建与构建](../development/setup-and-build.md)，发布见 [发布与部署](../release/release-and-deployment.md)。

## 1. 首次自检

```bash
dotnet --info
dotnet build AiAssistant.sln -c Release --warnaserror
dotnet test --solution AiAssistant.sln -c Release
dotnet run --project src/AiAssistant.Host/AiAssistant.Host.csproj -- --doctor
```

`--doctor` 输出加密库健康、Schema 版本、事件/媒体计数、模型端点能力、MCP 状态、PDF 字体；macOS 额外输出 Helper 状态、重启次数与 TCC 三项。库不健康时退出码 1。

## 2. 启停与单实例

```bash
dotnet run --project src/AiAssistant.Host/AiAssistant.Host.csproj
```

- 关闭窗口默认只隐藏（有托盘时），采集继续；显式「退出」才结束进程。
- 再次启动通过数据目录下 `instance.sock` 唤回已隐藏窗口，不会启动第二个采集器。
- GNOME 等无托盘环境关闭窗口即退出。

## 3. 维护命令

```bash
# 加密备份 / 恢复（恢复需显式确认）
AiAssistant --backup /safe/aiassistant-backup.zip
AiAssistant --restore /safe/aiassistant-backup.zip --confirm-restore

# 删除全部本地数据（两步确认）
AiAssistant --purge-data --confirm-purge

# 重建向量索引
AiAssistant --rebuild-vectors

# 区间报告 / 证据问答
AiAssistant --report-range --from 2026-11-16 --to 2026-11-22
AiAssistant --ask "本周数据库迁移进展如何" --days 7
```

- 桌面端「数据与导出」页同样可以导出加密备份（ZIP）并发起恢复：恢复会先校验备份，重启应用后才覆盖当前数据。
- 无证据时问答明确降级并以退出码 1 结束。
- 报告默认写入 `artifacts/test-results/reports/`；可用 `--output` 覆盖。
- 备份包含 SQLite3MC 加密库与媒体密钥引用；恢复会替换现有数据，务必先备份。

## 4. 模型密钥

```bash
echo "sk-..." | AiAssistant --import-model-secret corp-api-key
AiAssistant --list-model-secrets
AiAssistant --delete-model-secret corp-api-key --confirm-delete-secret
AiAssistant --probe-model
```

密钥值只写不读；`models.json` 使用 `apiKeySecret` 引用名称。诊断页提供同样的保存/刷新/删除能力。

## 5. 本地 MCP

```bash
AIASSISTANT_MCP_ENABLED=true AiAssistant --mcp-serve
AiAssistant --mcp-token
```

- 仅监听 `127.0.0.1`，默认端口 `47831`，Bearer 令牌 + Origin/Host 白名单 + 限流。
- 只暴露 `search_events`、`activity_stats`、`list_reports`、`get_report` 四个只读工具。
- 联调客户端见 `tools/mcp/`；协议细节见 [安全与隐私](../release/security-and-privacy.md)。

## 6. 诊断与日志

- GUI「本地诊断」：平台能力矩阵、模型端点、MCP 状态、最近任务与脱敏结构化日志。诊断导出位于页面末尾卡片：先生成脱敏预览（仅元数据，不含正文、密钥与完整路径），再选择 Markdown / SVG / PNG / PDF / JSON 保存；JSON 写盘内容即用户确认的预览，其他格式由同一预览生成。最近任务默认展示 5 条，完整记录见脱敏导出。
- 日志只记录元数据，不含正文、密钥或完整路径；空 `catch` 均经 `SuppressedExceptionLog` 记录。
- 日志与导出默认位于数据目录；E2E 截图/报告位于 `artifacts/test-results/e2e/`。

## 7. 升级、回滚与卸载

| 平台 | 升级 | 回滚 | 卸载 |
|---|---|---|---|
| Windows | 新 MSI 覆盖安装（MajorUpgrade） | 卸载后安装旧 MSI | 应用与功能；数据目录默认保留 |
| macOS | 新 `.pkg`/`.app` 替换 | 恢复旧 `.app` 或 pkg | 删除 `/Applications/AiAssistant.app` |
| Linux | 安装新 deb/tar | 重装旧 deb 或解压旧 tar | `apt remove aiassistant`；数据目录保留 |

升级前用 `--backup` 或复制数据目录；回滚后若 schema 已升级，使用对应版本备份恢复。完整签名、校验与卸载说明见 [发布与部署](../release/release-and-deployment.md)。

## 8. 平台排障

**macOS**

- 首次运行需授予屏幕录制、辅助功能、麦克风；`--doctor` 查看 TCC。
- Swift Helper 未随包或不可执行时相关能力降级；`bash build/native/mac-helper.sh build` 后重打包。
- Helper socket 位于用户私有 `$TMPDIR`，不落到共享 `/tmp`。

**Linux**

- 依赖：`libsecret-1-0`、`libx11-6`、`libice6`、`libsm6`、`libfontconfig1`；Wayland 抓屏需 `xdg-desktop-portal` + `pipewire`。
- portal 对话框必须批准；`AIASSISTANT_LINUX_BACKEND=X11` 可临时回退。
- `AIASSISTANT_WAYLAND_PIPEWIRE=false` 关闭 PipeWire，退回按需 portal 截图。

**Windows**

- 数据目录 `%LOCALAPPDATA%\AiAssistant`；MSI 为 `perMachine`，需管理员。
- 未签名包会被 SmartScreen/证书信任拦截；开发自签名产物带 `-dev`。

性能、浸泡与基准口径见 [测试与评测](../development/testing-and-benchmarks.md)。
