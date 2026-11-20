# 升级、备份、恢复与卸载

> 本文档中的命令均使用安装目录内的 `AiAssistant.Host`（Windows 为 `AiAssistant.Host.exe`）。
> 数据目录默认：`%LOCALAPPDATA%\AiAssistant`、`~/Library/Application Support/AiAssistant`、`~/.local/share/AiAssistant`；
> 可用 `AIASSISTANT_DATA_DIRECTORY` 覆盖。以下示例用 `$DATA` 表示该目录。

## 1. 升级策略

- **Schema 只向前迁移**：`SchemaMigrator` 按版本顺序执行迁移，每步单独事务，失败回滚并抛 `SchemaMigrationException`。
- **拒绝降级写入**：数据库版本高于当前应用时，应用拒绝启动写入，避免旧代码破坏新结构。
- **已验证迁移**：v1（S1 初始结构）→ v2（S7 语音 `audio_content_id`）由 `ReleaseReadinessTests.SchemaUpgrade_FromV1ToV2_PreservesEventsAndVoiceCommands` 覆盖，事件与语音日志均保留。

升级步骤：

1. 退出应用（托盘 → 退出）。
2. 备份：`AiAssistant.Host --backup <备份文件路径>`（实测生成 84 KB 加密备份，含完整 Schema）。
3. 安装新版本（覆盖安装或替换发布目录）。
4. 首次启动执行迁移；`--startup-check` 应输出 `startup-check OK`。
5. 打开时间线、日报、检索页各抽查一条数据。

> 升级前不要删除 `keystore` 目录；密钥丢失会导致历史数据无法解密（应用会给出可诊断错误，而不是静默损坏）。

## 2. 备份

```bash
AiAssistant.Host --backup /path/to/AiAssistant-$(date +%Y%m%d).enc
```

- 备份为加密 SQLite 文件，使用当前系统密钥库中的主密钥加密；换机恢复需要同一密钥库或重新绑定密钥。
- 备份不包含 `models.json`、模型端点密钥与日志；这些需单独保管。

## 3. 恢复

```bash
AiAssistant.Host --restore /path/to/AiAssistant-20261120.enc --confirm-restore
```

- 恢复前会校验备份可解密且 `PRAGMA integrity_check = ok`，并拒绝来自更高 Schema 版本的备份。
- 未带 `--confirm-restore` 时命令拒绝执行并返回退出码 3。
- 恢复会覆盖当前数据库并重新执行迁移；建议先对当前库再做一次备份。
- 实测：备份 → 恢复 → `--startup-check` 全部成功，恢复后数据库可正常读写。

## 4. 删除数据

图形界面：「数据与导出」页 → 选择日期删除，或「清空全部本地数据」，均需二次确认。

命令行（无界面/卸载前）：

```bash
# 仅清空采集数据（事件、媒体、日报、任务、语音日志、向量索引）
AiAssistant.Host --purge-data --confirm-purge
```

- 不会删除系统密钥库条目、`models.json` 与日志文件。
- 删除后数据库仍可正常使用（由 `ReleaseReadinessTests.PurgeAll_RemovesEveryTableAndBlob_AndDatabaseStaysUsable` 覆盖）。

## 5. 卸载

### Windows

1. 设置 → 应用 → 时光 → 卸载（或删除自包含发布目录）。
2. 可选彻底清除：
   - 删除 `%LOCALAPPDATA%\AiAssistant`
   - 删除凭据管理器中与 `AiAssistant` 相关的条目（DPAPI 保护的密钥文件位于 `%LOCALAPPDATA%\AiAssistant\keystore`，删除目录即可）

### macOS

1. 将 `AiAssistant.app` 拖入废纸篓。
2. 可选彻底清除：
   - 删除 `~/Library/Application Support/AiAssistant`
   - 打开「钥匙串访问」，删除 `com.aiassistant.*` 条目
   - `launchctl bootout gui/$(id -u) ~/Library/LaunchAgents/com.aiassistant.*.plist`（如安装过自启）

### Linux

```bash
sudo apt remove aiassistant        # 使用 .deb 安装时
rm -rf ~/.local/share/AiAssistant  # 可选：删除数据
rm -f ~/.config/autostart/aiassistant.desktop
secret-tool clear service aiassistant  # 可选：删除 libsecret 条目
```

## 6. 卸载前检查

- [ ] 已导出需要保留的日报 Markdown、问答与周报。
- [ ] 已确认不再需要历史截图/音频（删除后不可恢复）。
- [ ] 已记录 `models.json` 中的端点配置（不含明文密钥）。
- [ ] 已退出应用与托盘进程，避免文件占用。

## 7. 退出码

| 退出码 | 含义 |
|---|---|
| 0 | 成功 |
| 2 | 启动/维护失败（权限、磁盘、密钥、数据库损坏） |
| 3 | 参数错误或缺少确认开关 |
