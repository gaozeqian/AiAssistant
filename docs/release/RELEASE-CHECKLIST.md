# 发布检查表（Release Checklist）

> 每次发布前逐项勾选并附证据（命令输出、运行链接或截图路径）。未完成项必须在 `KNOWN-LIMITATIONS.md` 中登记。

## 1. 代码与自动化门禁

- [ ] `dotnet build AiAssistant.sln -c Release`：0 警告 0 错误。
- [ ] `dotnet test AiAssistant.sln -c Release`：全部通过（当前基线 176 项）。
- [ ] 架构依赖测试 6 项通过（`Architecture.Tests`）。
- [ ] 三平台 CI 全绿（Windows / macOS / Linux）；若 Actions 配额或账单异常，记录本地替代证据。
- [ ] SBOM 生成成功（CycloneDX）并附在发布产物中。
- [ ] 依赖清单与许可证矩阵已更新（`docs/compliance/DEPENDENCY_MANIFEST.md`）。

## 2. 打包与签名

- [ ] Windows：`pwsh build/package/package-windows.ps1 -RequireSigning` 产出已签名 zip；`package-windows-msi.ps1` 产出已签名 MSI。Windows 只发布 zip 与 MSI，不构建 MSIX。
- [ ] Windows：`signtool verify /pa` 对 zip 内 exe 与 MSI 安装包均通过。
- [ ] macOS：`swift build -c release` 成功，`package-macos.sh --require-sign` 完成 codesign + notarytool + stapler。
- [ ] macOS：`codesign --verify --deep --strict` 与 `spctl --assess` 通过。
- [ ] Linux：`package-linux.sh` 产出 tar.gz 与 .deb；`dpkg-deb --info` 显示正确版本与依赖。
- [ ] 安装包体积与 `PERFORMANCE.md` 记录一致（显著增大需说明原因）。
- [ ] 安装包内含 `models/sherpa`（KWS/ASR/MeloTTS/VAD，约 268MB、26 个文件）；对话/Embedding 模型仍保持外部。
- [ ] 安装包内含法务文件：`LICENSE`、`EULA.md`、`THIRD-PARTY-NOTICES.md`、`THIRD-PARTY-NOTICES-ONNXRUNTIME.txt`（Windows 安装根、macOS `Contents/Resources`、Linux `/usr/lib/aiassistant` 与 `/usr/share/doc/aiassistant`），且第三方声明与当前模型版本一致。
- [ ] 空数据目录启动后语音页显示「本机唤醒与语音命令已就绪」，`--benchmark-asr` 不报引擎缺失。

## 3. 安装 / 升级 / 卸载实测

- [ ] 全新安装后首次启动成功，`--startup-check` 输出 OK，`--doctor` 显示存储健康。
- [ ] Windows zip 解压后 `--version`/`--smoke-check`/`--startup-check` 均通过，`SHA256SUMS` 与产物一致。
- [ ] MSI：签名校验通过、安装/启动/卸载成功，卸载后 `%LOCALAPPDATA%\AiAssistant` 与密钥保留。
- [ ] 从上一版本覆盖升级：Schema 迁移成功，抽查事件/日报/语音日志。
- [ ] 备份 → 恢复 → 启动成功（`--backup` / `--restore --confirm-restore`）。
- [ ] 卸载后按文档清理数据目录与密钥库条目，确认无残留进程与自启项。
- [ ] 升级/卸载记录写入发布说明。

## 4. 故障注入

- [ ] 磁盘配额耗尽：写入抛 `StorageCapacityException`，数据库未损坏。
- [ ] 数据库文件损坏：启动给出可诊断错误，不覆盖原文件。
- [ ] 密钥不可用/被删除：`StorageAuthenticationException`，数据库字节未被改写（`MissingKeyMaterial_FailsDiagnosably_WithoutCorruptingDatabase`）。
- [ ] 模型离线：日报/问答降级为确定性统计或明确「无证据」，不阻塞采集。
- [ ] macOS Helper 崩溃：监督器指数退避重启，UI 不崩溃。
- [ ] MCP 未授权/非 loopback/超限：分别 401 / 拒绝 / 429。

## 5. 浸泡与性能

- [ ] 24 小时浸泡（待机 + 活跃 + 休眠唤醒）通过，控制事件零丢失。
- [ ] 多日浸泡通过：`AIASSISTANT_SOAK_DAYS=14 dotnet test tests/AiAssistant.Platform.Tests -c Release --filter "FullyQualifiedName~MultiDayAcceleratedSoak"`（约 75 秒），报告写入 `artifacts/soak/`。
- [ ] 真实时间浸泡通过：`AIASSISTANT_REAL_SOAK_MINUTES=90 dotnet test tests/AiAssistant.Platform.Tests -c Release --filter "FullyQualifiedName~RealTimeSoak"`，报告写入 `artifacts/soak/soak-real-90m.md`。
- [ ] 插拔显示器/麦克风后采集与语音可恢复。
- [ ] 启动耗时、包体积、测试耗时更新到 `PERFORMANCE.md`。
- [ ] 运行 `AiAssistant.Host --benchmark-model --output artifacts/benchmarks/model.md` 并归档；成功率与 JSON 合规率达到发布阈值。
- [ ] 运行 `dotnet test tests/AiAssistant.Report.Tests --filter "FullyQualifiedName~S9RetrievalQuality"`，归档 `artifacts/benchmarks/retrieval-benchmark.md`。
- [ ] 配置真实 embedding 端点后运行 `AiAssistant.Host --benchmark-retrieval --output artifacts/benchmarks/retrieval-real.md` 并归档。
- [ ] 更换 embedding 模型或文档格式后运行 `AiAssistant.Host --rebuild-vectors`，确认索引版本与维度正确。
- [ ] 用 `--import-model-secret` 导入企业端点密钥，`--probe-model` 确认各档位能力，再删除测试密钥。
- [ ] 企业网络下配置 `enterpriseCaPath`/`clientCertificatePath`/`proxyUrl`，确认 `--probe-model` 的 CorporateIntranet 全能力为 True。
- [ ] 启动 `--mcp-serve` 后运行 `tools/mcp/inspector-smoke.ps1`，确认握手、4 个只读工具与工具调用通过。
- [ ] 生成周报并确认 Markdown、stats JSON、SVG、PNG、PDF 五个文件均可打开且中文无乱码；PDF 文本可选中/检索；`--doctor` 的 `pdfFont` 指向实际字体。
- [ ] 用 `--report-range` 与 `--ask` 在无 UI 环境各执行一次，确认退出码与导出文件正确。
- [ ] 内存曲线无明显增长（记录峰值与采样方式）。

## 6. 隐私与无障碍

- [ ] `PRIVACY.md` 与实际行为一致（采集项、外发条件、保留期、删除路径）。
- [ ] 诊断导出预览不含正文、密钥、完整路径；确认导出后文件同样脱敏。
- [ ] 键盘可完成主要流程：Ctrl+D1…D7 切页、Ctrl+F 检索、F5 刷新、Tab 顺序合理。
- [ ] 关键按钮与输入框具备无障碍名称。
- [ ] 自动化门禁通过：`dotnet test tests/AiAssistant.Platform.Tests --filter "FullyQualifiedName~AccessibleTab|FullyQualifiedName~PrimaryControls|FullyQualifiedName~KeyboardShortcuts"`。
- [ ] 权限缺失时诊断页给出可执行的修复建议。
- [ ] 数据删除需二次确认，删除后 UI 状态正确刷新。

## 7. 文档与回滚

- [ ] `KNOWN-LIMITATIONS.md` 与实际限制一致。
- [ ] `UPGRADE-AND-UNINSTALL.md` 命令在目标平台实测。
- [ ] `ROLLBACK.md` 的回滚步骤可执行，上一版本安装包与备份可用。
- [ ] 发布说明包含版本号、变更摘要、已知问题、校验和（SHA256）。

## 8. 发布签署

| 项目 | 负责人 | 日期 | 结论 |
|---|---|---|---|
| 代码门禁 | | | |
| 打包签名 | | | |
| 安装升级卸载 | | | |
| 故障注入 | | | |
| 浸泡性能 | | | |
| 隐私无障碍 | | | |
