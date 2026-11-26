# 变更记录

本文件记录 AiAssistant 的用户可见变更。格式遵循 [Keep a Changelog 1.1.0](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [SemVer 2.0.0](https://semver.org/lang/zh-CN/)。

版本号以仓库根 `Directory.Build.props` 的 `VersionPrefix` 为准，本文件首条必须与之一致。

## [0.1.4] - 2026-11-26

修复诊断导出、平台能力操作按钮、后台任务列表与周报 PDF 的显示问题，三平台交付物形态、安装与升级方式不变。

### Changed

- 日报定时：默认改为每天 00:30 或启动时检查前一天；当天无活动记录不生成，已有日报但生成后又有活动记录时自动追加新版本；日报页新增「自动日报」卡片（时间与启停，持久化到数据目录），环境变量仅在未通过界面保存时作为初始值。

### Fixed

- 诊断页导出：说明、预览与保存合并为同一张卡片并置于页面末尾；未生成预览时不能保存，且保存内容与用户确认的预览严格一致。
- 诊断页标题栏：右侧预留滚动条宽度，「重新检查」按钮不再被垂直滚动条遮挡。
- 平台能力：操作按钮按能力状态与处理建议匹配动作（权限授权、前往语音页、重新检查），信息性降级不再显示无效按钮；操作列加宽、文案缩短以避免裁剪；建议文案不再暴露环境变量名。
- 最近后台任务：默认只显示最近 5 条并标注总数，脱敏导出仍保留完整记录。
- 周报 PDF：由单页柱状图改为含概览、图表、每日统计表、应用分布与代表性记录的可搜索文档；PNG/SVG 仍为图表导出。
- 语音状态：启用后徽章与语音页显示「已启用 · 待唤醒」，唤醒词命中后才变为「正在聆听」，不再与开关状态矛盾。

## [0.1.3] - 2026-11-26

修复 0.1.2 中两处 UI 交互回归，三平台交付物形态、安装与升级方式不变。

### Fixed

- 检索 / 日报页进度条：撤销切页清 `IsBusy` 的处理，恢复忙碌态语义（切页返回后进度条正常显示且不会重复触发操作）；改用定时器驱动的确定性循环进度，规避不确定进度条在 Tab 隐藏/重现后动画冻结。
- 平台能力表格：操作列由自适应改为固定宽度并补表头，消除逐行独立 Grid 导致的列错位。

## [0.1.2] - 2026-11-26

桌面端 UI 显示与交互体验修复，三平台交付物形态、安装与升级方式不变。

### Added

- 导出：所有导出改为用户选择保存位置与文件类型；周报支持 Markdown / SVG / PNG / PDF / 统计 JSON 单格式导出。
- 模型配置：新增「保存并重启」，保存后由应用自行拉起新进程使配置生效，无需手动退出再启动。
- 诊断：平台能力对需要额外操作的能力提供「打开系统设置 / 去授权」可点击动作（Windows）。
- 日报 / 周报：正文与周报改以 Markdown 渲染预览（引入 `Markdown.Avalonia`）。

### Changed

- 本地 MCP 启用改为 UI 开关（ToggleSwitch）并持久化，不再依赖环境变量文案；环境变量显式设置仍优先。
- 开机自启、启用语音改为 ToggleSwitch；采集页快照 / 黑名单 / 自启状态各归其位。
- 移除独立的「模型端点密钥」与「权限引导」卡片：密钥内聚到模型配置，权限提示并入平台能力列表。
- 平台名称收敛为 `Windows` / `Linux` / `macOS`；UI 文案去除 `models.json`、`apiKeySecret` 等实现细节。
- 诊断页日志与后台任务增加列头与时间日期；最后刷新时间显示日期。

### Fixed

- 本机模型网关不显示向量模型名称（组合网关探测快照补入 embedding 模型名）。
- 端点能力重复显示（按角色去重，去除布尔诊断串）。
- 检索 / 日报页进度条切页返回后动画卡住（切页复位非当前页加载态）。
- 后台任务首末列日期不一致（区分报告日期与创建时间并加列头）。

## [0.1.1] - 2026-11-24

工程标准化收口与依赖升级。三平台交付物形态、安装与升级方式不变。

### Added

- 工程底座：统一 `artifacts/` 输出、`nuget.config` 源映射与全量 NuGet 审核、CycloneDX 1.6 SBOM（含 Swift helper 与本地模型 ML-BOM）。
- 测试栈：`xunit.v3` + Microsoft.Testing.Platform 原生运行器，根除 VSTest 回环 Socket 崩溃；全量测试在本机原生通过。
- 安全：本地 MCP 服务新增 `Origin` / `Host` 白名单校验、`MCP-Protocol-Version` 协商，并将鉴权严格前置于限流之前。
- 架构门禁：16 模块依赖矩阵、生产程序集禁引测试/基准类型、生产代码禁直接调用系统时钟的自动化断言。
- 测试基建：新增 `AiAssistant.Testing.Common`（FakeClock、TempArtifactDirectory、ProcessEnvironmentScope、AsyncAssert），测试工程按模块拆为 15 个，新增 Desktop/Host/Application/Models/LocalApi/Soak 测试工程。
- 测试分层：统一 `Category` / `OS` Trait，受限环境用例改为 xunit.v3 显式 Skip；CI 输出 Cobertura 覆盖率到 `artifacts/test-results/coverage/`。
- 原生测试：`native/AiAssistant.MacHelper` 新增 `MacHelperTests`（IPC、权限快照、音频参数、固定时间比较）。

### Changed

- 模型接入统一为 OpenAI 兼容协议：移除 Ollama 原生 `/api/chat`、`/api/embed`、`/api/tags` 路径与 `provider` 配置，所有端点统一调用 `/v1/*`；`models.json` 升级为 schema 3（`1`/`2` 仍可读取并在保存时升级）。思考开关映射为 `reasoning_effort`，本机端点关闭思考时发送 `none`，避免推理型模型返回空内容；远程端点仅在显式开启思考时发送。
- 时钟访问统一收敛到 `IClock`（对齐 `System.TimeProvider`）；模型地址与默认模型名收敛到 `ModelDefaults`。
- E2E 自驱动与基准命令从生产交付剥离到 `tools/AiAssistant.Diagnostics.Host`。
- 三平台交付矩阵四处文档/CI 对齐：Windows `.zip` + `.msi`、macOS `.pkg` + `.zip`、Linux `.tar.gz` + `.deb`。
- 自签名根证书导入仅在显式 `--dev-self-signed` 模式下发生，正式 CA / Developer ID 路径物理隔离。
- CI 补齐最小权限、并发控制、作业超时、Action SHA 锁定与格式/ShellCheck 检查。
- 产物布局收口为 `artifacts/{bin,obj,publish,staging,dist,cache,test-results,tmp}`；SwiftPM scratch 与测试临时目录全部移出源码树。
- 发布态二进制统一为 `AiAssistant.exe` / `AiAssistant` / `aiassistant`；安装包改为 Windows/MSI 共享载荷、macOS `macos-arm64` 命名、Linux Debian Policy 命名与顶层目录便携包；开发自签名产物统一 `-dev`。
- 组合根：CLI 拆为独立 `ICliCommand`（`Program.cs` ≤150 行），`RuntimeServices` 类型化服务图，`DesktopLaunchContext` 不可变启动上下文。
- UI 拆分：`MainWindow.axaml` 拆为共享样式 + 7 个 Tab UserControl；Diagnostics/Timeline ViewModel 抽出无状态映射，单文件 ≤350 行。
- 重型模块拆分：MCP 传输/鉴权/生命周期、采集样本处理/控制审计、macOS 采集适配器、语音音频/反馈/日志、PipeWire 回调、Application 端口与 Swift 原生能力按职责拆分。
- 文档从 44 篇收敛为 25 篇：架构合并为 `overview.md`，开发规范与测试聚合，发布/安全/合规/路线图重写；删除归档、空壳索引与手工状态抄写。
- 依赖升级：`Microsoft.Extensions.AI` 11.0.1、`Microsoft.Extensions.Hosting` 11.0.0、`Microsoft.Data.Sqlite.Core` 11.0.0、`org.k2fsa.sherpa.onnx` 2.0.3；随包原生 ONNX Runtime 保持 1.28.2，与托管绑定 `Microsoft.ML.OnnxRuntime.Managed` 1.28.0 的 ABI 对齐。
- CI：GitHub Actions 统一升到 Node 24 运行时（`checkout` 7.0.1、`setup-dotnet` 6.1.0、`cache` 6.1.0、`upload-artifact` 7.0.2、`download-artifact` 8.0.2、`action-gh-release` 3.0.4），上传与下载产物两端同步升级。

### Fixed

- `ModelHttpClientFactory` 中客户端证书与企业 CA 句柄随 Handler 释放，消除长期存活闭包导致的资源泄漏。
- 清理重复图标资产、UTF-8 BOM、阶段代号测试命名与 GNU 专属脚本命令。
- 治理全部静默 catch（统一脱敏日志）、收敛 21 个内部实现类型为 internal sealed、拆分 7 个上帝类为职责清晰的组件。
- 测试中所有 `if (!环境) return;` 改为显式 `Assert.Skip`；测试临时文件不再写入 `AppContext.BaseDirectory/test-temp` 或硬编码 `/tmp`。
- 修复 macOS Helper socket 回退共享 `/tmp` 的问题；Linux 语音模型安装路径按 XDG 规范解析。
- 修复 `PngCodec`/TTS 路径分隔符在 Windows 上的跨平台差异。
- 时间线：改为倒序展示（首行为最新记录）、底部「加载更早」按页追加（每页 200），移除单日 1000 条硬上限；分页改用 `(occurredAt, id)` 键集游标，翻页期间产生的新记录不会造成重复或漏行。旧实现按时间升序截断，超过上限时丢失的是最新记录。
- 时间线：修复刷新后再选中记录无法预览媒体——媒体取消令牌释放后未清空，下一次选中在已释放实例上 `Cancel()` 抛 `ObjectDisposedException` 并被后台任务吞掉。
- 采集：多显示器改为逐屏截图、逐屏记录（前台窗口所在屏保留窗口上下文，其余记为桌面快照），事件元数据记录显示器标识与边界，时间线详情显示「显示器 N」。
- 模型配置：`models.json` 升级为 schema 2，仅保存当前在用的对话与向量模型且二者完全独立（同档位不同地址不再丢失向量配置）；本地模型不再强制，schema 1 仍可读取并在保存时升级。
- 模型配置：UI 新增对话/向量接口密钥输入（只写不读，写入加密库并以 `apiKeySecret` 引用），修复公网/内网端点无法配置密钥的问题。
- 测试基建：修复 headless UI 测试在 `await` 之后吞掉断言异常的问题（`Func<Task>` 绑定到同步重载返回 `Task<Task>`），并让测试宿主加载真实 Fluent 主题，窗口视觉树才会生成。
- CI：修正流水线触发分支为实际默认分支 `master`。此前 `on.push` / `on.pull_request` 误写为 `main`，而仓库不存在 `main`，导致推送与 PR 均不触发 CI。
- 工程：仓库规范审计脚本的 29 处 `&& pass || fail` 改为显式 if/then/else（`pass` 返回非零时 `fail` 会被连带执行）；macOS 自签名脚本的钥匙串参数改用数组传递，路径含空格不再被拆断。

## [0.1.0] - 2026-11-22

首版。三平台本机活动追踪与日报，全部推理在本机完成。

### Added

- 采集：前台窗口与焦点、用户白名单目录内的文件活动、屏幕截图（按需与连续两档），全部走平台适配器。
- 存储：SQLite3MC 加密数据库、系统密钥库托管的密钥、媒体文件 AES-GCM 加密、`sqlite-vec` 向量索引与降级兜底。
- 汇总：三段式触发（定时 / 空闲 / 关机补偿）生成日报，日报版本化并保留历史。
- 展示：Avalonia 桌面端（时间线、检索与问答、隐私与控制、语音、数据与导出、本地诊断、日报共七页），支持 macOS / Windows / Linux。
- 模型：统一 `IChatClient` 网关，支持本机 Ollama、内网端点与企业公网端点三档策略；默认对话模型 `gemma4:12b`，默认 embedding 模型 `embeddinggemma-2:740m`。
- 语音：sherpa-onnx 唤醒词 + Paraformer ASR + MeloTTS 合成 + Silero VAD，唤醒词「你好时光」。
- 导出：Markdown / SVG / PNG / PDF 四类区间报告，附 stats JSON。
- 集成：只读本地 MCP 服务（默认关闭）、`--doctor` 自检、备份 / 恢复 / 清空数据等维护命令。
- 打包：Windows `zip` + `MSI`、macOS `.pkg` + `.zip`、Linux `tar.gz` + `.deb`；安装包随带 KWS / Paraformer / MeloTTS / Silero VAD 四类模型资产。

### Changed

- 随包 TTS 由 `vits-zh-aishell3` 更换为 MeloTTS（MIT）；旧权重归档至 `artifacts/test-results/retired-models/`，不再参与打包。
- Windows 交付格式收敛为 `zip` 与 `MSI`，MSIX 打包链路下线。

### Security

- 随包资产许可收敛到 MIT / Apache-2.0 白名单；`THIRD-PARTY-NOTICES.md` 收录 Paraformer 三级归属链与四份许可证全文。
- 依赖许可门禁：仅允许 MIT / Apache-2.0 / BSD-2-Clause / BSD-3-Clause。
