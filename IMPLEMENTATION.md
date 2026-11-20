# AI 个人活动追踪助手 · 分步骤实现方案

> 版本：v1.0 ｜ 日期：2026-11-19 ｜ 基线：`DESIGN.md` v2.5
>
> 本文是工程实施计划，不重复产品设计；重点定义架构边界、实现顺序、阶段交付物、
> 验证方法与回退策略。实施中若发现底层技术组合不成立，先回到对应 POC，不用业务代码掩盖问题。

---

## 1. 实施目标与交付策略

### 1.1 目标

交付一个可长期常驻的跨平台桌面应用：

- 默认本地采集、加密存储和离线模型，数据边界清晰。
- Windows 先形成完整闭环，再扩展 macOS 与 Linux。
- 日报支持手动、启动补偿、定时三通道，任务可恢复、结果可追溯。
- 默认唤醒词"你好时光"，唤醒始终本机；语音命令与问答复用业务用例。
- 任一模型、平台采集器、向量实现均可替换，不影响领域核心。

### 1.2 交付策略

采用**垂直切片 + 阶段门禁**，不按"先写完全部采集器、再写完全部 UI"横向铺开：

```
最小闭环：事件采集 → 加密落库 → 时间线回看
      ↓
平台闭环：macOS / Linux 接入同一套用例与契约测试
      ↓
智能闭环：预处理 → 模型网关 → 日报 → 补偿恢复
      ↓
交互闭环：本地唤醒 → 基础命令 → 带证据的完整语音对话
```

每个阶段必须可运行、可测试、可回退；不以"代码已写完"作为完成标准。

---

## 2. 架构设计原则及落地方式

### 2.1 模块化单体，而非过早微服务化

桌面端主体保持单进程模块化单体，降低部署与事务复杂度；只有以下情况使用独立进程：

- macOS Swift helper：隔离原生框架和权限生命周期。
- 可选的本机模型服务：Ollama / LM Studio 本就是外部进程。
- 经 POC 证明有必要的崩溃隔离或原生依赖隔离。

模块间通过明确接口通信，禁止因为"同进程"而跨层直接访问实现类。

### 2.2 端口与适配器，依赖指向领域核心

- 领域模型、用例和端口不依赖 Avalonia、SQLite、Ollama、sherpa-onnx 或具体 OS API。
- SQLite、平台采集器、模型 Provider、系统密钥库都是可替换适配器。
- UI 只调用应用用例，不直接写 SQL、不直接启动模型、不直接调用平台 API。
- 依赖注入集中在一个 Composition Root，业务模块不自行创建全局服务定位器。

### 2.3 高内聚、低耦合，按业务变化划分模块

- Capture 负责"何时采集"，Processing 负责"如何理解"，Storage 负责"如何可靠保存"。
- Summarization 负责日报算法，Scheduler 负责任务触发与恢复，两者不得互相嵌入实现细节。
- Voice 负责语音会话与意图路由，不复制日报、查询、暂停等业务逻辑，只调用已有用例。
- Desktop 负责呈现与交互，不在 ViewModel 中堆业务规则。

### 2.4 接口隔离与替换成本前置

接口围绕调用方需求定义，避免一个巨型 `IPlatformService`：

- `IScreenCapture`、`IWindowActivitySource`、`IFileActivitySource`、`IAudioCapture` 分离。
- `IMediaStore`、`IKeyStore`、`IVectorIndex`、`IEventRepository` 分离。
- 模型业务直接依赖 Microsoft.Extensions.AI 标准接口，自定义接口只覆盖路由和策略等自身职责。
- 每个适配器都运行同一套契约测试，替换实现时无需修改用例。

### 2.5 默认安全、最小权限、隐私内建

- 默认不启用公网、不全盘监控、不记录语音命令内容、不保存唤醒前音频。
- 采集目录、应用黑名单、权限和模型档位都由明确策略决定。
- 密钥、正文、Token 不进日志；审计只记录元数据。
- 权限被拒绝时只降级相关能力，不导致整个应用不可用。

### 2.6 幂等、背压与故障恢复是一等设计

- 所有后台任务都有稳定 ID、状态机、重试次数和恢复语义。
- 采集管道使用有界队列；过载时合并或丢弃低价值重复帧，不能无限占用内存。
- 控制、删除、审计事件不能被普通采集流量挤掉。
- 应用异常退出、休眠唤醒、模型断连、权限撤销都属于正常测试场景。

### 2.7 KISS / YAGNI 与演进式架构

- 不引入独立向量数据库、消息中间件、Quartz.NET 或通用插件平台，除非实测证明需要。
- 不用复杂事件溯源框架；持久事件表和明确状态机已经足够。
- 通过 ADR 记录重要取舍，通过适配器与契约测试保留未来替换能力。

---

## 3. 物理架构与依赖约束

### 3.1 项目划分

在 `DESIGN.md` 的逻辑结构上，实施时补充 Application 与 Host 两个物理边界：

```
AiAssistant.sln
├── AiAssistant.Core            # 实体、值对象、领域事件；零第三方依赖
├── AiAssistant.Application     # 用例、端口、DTO、策略；只依赖 Core
├── AiAssistant.Capture         # 采集编排、去重、背压、黑名单
├── AiAssistant.Processing      # OCR/ASR/VAD/脱敏/说话人分离流水线
├── AiAssistant.Storage         # SQLite3MC、媒体加密、向量、迁移、备份
├── AiAssistant.Models          # Microsoft.Extensions.AI 适配与策略装饰器
├── AiAssistant.Summarization   # 分段、map-reduce、结构化校验
├── AiAssistant.Scheduler       # 三通道触发、持久任务队列、补偿
├── AiAssistant.Voice           # KWS、语音会话状态机、意图路由、TTS
├── AiAssistant.Platform.*      # 三平台端口实现
├── AiAssistant.Desktop         # Avalonia View/ViewModel；只调用 Application
├── AiAssistant.Host            # 启动、DI、生命周期、Composition Root
└── native/AiAssistant.MacHelper # Swift 原生辅助进程
```

> Host 可与 Desktop 共用最终可执行入口，但代码职责仍分离：Host 管组装和生命周期，
> Desktop 管 UI。不要为了让项目数量最少而把依赖组装写进 ViewModel。

### 3.2 允许的依赖方向

```
Core
  ↑
Application
  ↑
Capture / Processing / Summarization / Scheduler / Voice
  ↑                         ↘
Storage / Models / Platform.*   Desktop
  ↑                              ↑
  └──────────── Host ────────────┘
```

强制规则：

1. Core 不引用任何其他项目。
2. Application 不引用 Storage、Models、Platform、Desktop。
3. Desktop 不引用 Storage 和 Platform 实现。
4. Platform 项目之间互不引用。
5. 只有 Host 可以引用所有模块并完成注册。
6. 用架构测试在 CI 中验证以上规则，而非只靠 Code Review。

### 3.3 首批核心契约

先定义稳定契约，再并行实现适配器：

```csharp
// 示例仅表达职责，不要求逐字采用签名
public interface ICaptureSource : IAsyncDisposable
{
    SourceKind Kind { get; }
    CapabilityState Capability { get; }
    IAsyncEnumerable<ActivityEvent> CaptureAsync(CaptureContext context, CancellationToken ct);
}

public interface IEventRepository
{
    Task AppendBatchAsync(IReadOnlyList<ActivityEvent> events, CancellationToken ct);
    IAsyncEnumerable<ActivityEvent> QueryAsync(EventQuery query, CancellationToken ct);
}

public interface IAudioHub : IAsyncDisposable
{
    IAsyncEnumerable<AudioFrame> SubscribeAsync(AudioSubscription subscription, CancellationToken ct);
}

public interface IReportJobQueue
{
    Task<EnqueueResult> EnqueueAsync(ReportJobRequest request, CancellationToken ct);
}
```

其他端口：`IKeyStore`、`IMediaStore`、`IVectorIndex`、`IProcessingStage`、`ICapabilityProvider`、
`IVoiceSession`、`IClock`。接口必须支持取消和释放；长任务不能只依赖 `async void` 或火忘任务。

### 3.4 时间与数据一致性

- 事件同时保存 `occurred_at_utc`、`local_date`、`time_zone_id`、`utc_offset_minutes`。
- 查询、排序使用 UTC 时刻；日报归档使用事件发生时的本地日期，跨时区后不重写历史。
- 所有写库操作使用事务；事件与媒体引用要么同时可见，要么同时失败。
- 向量使用独立映射表，记录模型、维度、版本；更换嵌入模型后显式重建索引。
- Schema 使用顺序迁移脚本和版本表，升级失败不得继续以旧代码写新库。

### 3.5 平台能力模型

统一用能力状态驱动 UI 和调度，不在各业务模块散落 OS 判断：

```
Supported / PermissionRequired / Degraded / Unavailable / DisabledByPolicy
```

- Linux 默认原生 Wayland，初始化失败或用户指定时回退 X11/XWayland。
- Wayland 抓屏走 portal；窗口、全局热键不可用时显示 Degraded，并说明缺少哪些数据。
- macOS helper 崩溃只影响对应适配器，由监督器指数退避重启，UI 与历史数据仍可访问。

---

## 4. 阶段总览与门禁

| 阶段 | 对应设计路线 | 核心交付物 | 进入下一阶段的门禁 |
|---|---|---|---|
| S0 工程奠基 | P0 前置 | 解决方案、ADR、CI、测试骨架、POC 计划 | 三平台构建链路可复现，依赖或许可证无阻断 |
| S1 核心与存储 | P0 | 核心契约、加密库、媒体存储、迁移、备份恢复 | SQLite3MC + sqlite-vec POC 通过；错误密钥不可读 |
| S2 Windows 最小闭环 | P0 | Windows 采集、暂停/黑名单、时间线、托盘 | 全天运行不丢控制事件；异常退出后可恢复 |
| S3 三平台扩展 | P1 | macOS helper、Linux Wayland 默认与回退、能力矩阵 | 平台契约测试通过；权限/降级行为符合矩阵 |
| S4 预处理与音频 | P1 | OCR、VAD、ASR、说话人分离、单路 AudioHub | 中文语音 POC 通过；无麦克风独占和资源失控 |
| S5 本机模型与日报 | P2 | 模型网关、本机档、map-reduce、三通道日报 | 固定语料评测达标；关机补偿和幂等测试通过 |
| S6 内网与公网档 | P2.5 | 企业配置、策略锁定、能力探测、审计与回退 | 三道闸不可绕过；断网/内网故障行为正确 |
| S7 唤醒与基础命令 | P2.6 | "你好时光"、会话状态机、TTS、固定命令 | 中文 KWS 实测达标；唤醒前音频不落盘 |
| S8 智能检索与完整对话 | P3 | 语义搜索、证据问答、周报、只读 MCP、完整语音对话 | 答案可追溯；本地接口认证与权限测试通过 |
| S9 发布加固 | 全阶段 | 安装包、签名公证、迁移、崩溃恢复、性能报告 | 发布检查表全部通过，可回滚且不丢用户数据 |

---

## 5. 分步骤实施明细

### S0：工程奠基与风险前置

**任务**

1. 创建解决方案、项目依赖图、Directory.Build.props 和集中包管理。
2. 建立 ADR 目录，至少记录模块化单体、SQLite3MC、sherpa-onnx、Swift helper、Wayland 默认五项决策。
3. 配置格式化、静态分析、Nullable、TreatWarningsAsErrors 的分阶段策略。
4. 建立单元、集成、契约、架构、性能和人工平台测试分层。
5. 建立 CI 矩阵：Windows x64、macOS arm64、Linux x64；原生依赖按 RID 打包验证。
6. 建立依赖清单、许可证扫描和 SBOM 生成流程。

**架构验收**

- 违反依赖方向的测试代码会使 CI 失败。
- 空解决方案可在三个目标平台产出可启动壳程序。
- 不把 Avalonia WebView、NativeAOT 等非必要优化纳入首版默认路径。

### S1：核心契约与可靠存储

**任务**

1. 定义 ActivityEvent、ContentRef、CapabilityState、PrivacyLevel、ReportJob 等领域对象。
2. 实现 `IClock`、UTC/本地日期模型、稳定 ID 和 SchemaVersion。
3. 接入 Microsoft.Data.Sqlite.Core + SQLite3MC，封装连接、事务、迁移、WAL 与忙等待策略。
4. 实现 DPAPI / Keychain / libsecret 密钥适配器，以及密钥轮换和恢复流程。
5. 实现 AES-GCM 媒体存储、ContentRef 引用计数、按需解密与安全清理。
6. 接入 sqlite-vec，但以 `IVectorIndex` 隔离；记录模型和维度，提供索引重建。
7. 实现备份、恢复、完整性检查和保留策略的基础机制。

**POC / 验收**

- 三平台均能创建、打开和备份加密数据库；错误密钥、损坏文件、被撤销权限均有可诊断错误。
- sqlite-vec 与加密 SQLite 可共同加载；若失败，保留同接口的替代实现，不降低加密要求。
- 事务中断测试后不会出现"数据库有记录但媒体丢失"的可见半成品。
- 迁移、回滚、备份恢复均有自动化测试。

### S2：Windows 垂直切片与最小可用 UI

**任务**

1. 实现窗口/焦点、屏幕、白名单目录文件采集；文件内容读取遵守大小和类型限制。
2. 使用有界 Channel 建立采集管道，实现黑名单、暂停、去重、pHash 和定时兜底。
3. 无障碍树优先，Windows.Media.Ocr 兜底；两者通过统一文本提取端口输出。
4. 实现 EventRepository、媒体引用和基础保留任务。
5. 建立 Avalonia MVVM 壳：时间线、事件列表、暂停状态、权限/错误提示。
6. 实现托盘、退出清理、开机自启可选开关和应用异常恢复。
7. 加入基础结构化日志与本地诊断页，日志不记录正文和密钥。

**验收**

- 完成"操作电脑 → 事件落库 → 拖动时间线回看"闭环，无需模型。
- 黑名单应用和暂停期间不生成对应内容；恢复后不补采暂停期间数据。
- 队列过载时优先合并重复帧，控制事件仍可执行，内存有明确上限。
- 强制结束进程后再次启动，数据库和任务状态可恢复。

### S3：macOS 与 Linux 平台扩展

**macOS**

1. 先定义版本化 IPC 协议、握手、鉴权、心跳、超时和 helper 崩溃监督。
2. Swift helper 逐项实现 ScreenCaptureKit、NSWorkspace/AX、CoreAudio、FSEvents、Vision。
3. 主进程只实现 IPC 适配器；统一处理权限拒绝、helper 重启和协议不兼容。
4. 主应用与 helper 统一签名、公证和权限声明。

**Linux**

1. 默认显式启用 `Avalonia.Wayland + UseWayland()`；实现 X11/XWayland 回退和设置项。
2. 使用 Tmds.DBus 对接 xdg-desktop-portal，保存可恢复授权信息并处理失效。
3. 实现 PipeWire/PulseAudio、AT-SPI、inotify；X11 回退实现 EWMH 和 XGetImage。
4. 对窗口、热键、托盘、多显示器建立真实能力矩阵，不伪装为全功能。

**验收**

- 三个平台复用同一 Application 用例和同一套适配器契约测试。
- 原生 Wayland 默认启动、失败回退、用户强制后端均经过验证。
- 权限撤销、helper 崩溃、portal 授权失效只造成明确降级，不导致数据损坏或整个应用退出。

### S4：预处理流水线与统一音频入口

**任务**

1. 定义可组合的 `IProcessingStage`，每阶段有输入输出类型、超时、取消和资源声明。
2. OCR、去重、脱敏、转写、说话人分离分别实现，不做一个不可拆分的巨型处理器。
3. 实现 AudioHub：只打开一次麦克风，向 KWS、VAD、命令 ASR 分发订阅流。
4. 常驻链路只保留 KWS 与必要 VAD；完整 ASR、说话人分离和高质量批处理按需启动。
5. sherpa-onnx 负责默认中文 ASR；Whisper.net 作为可配置高质量批处理适配器。
6. 建立中文语音测试集：安静/嘈杂、不同麦克风、中英混说、近音词、他人说话。

**验收**

- 多订阅者不会争用麦克风，取消订阅后资源能释放。
- 唤醒前环形缓冲只在内存，关闭语音后缓冲和监听资源均释放。
- 预处理失败不会阻塞原始事件的保存；资源超限时有背压和延迟处理策略。
- 输出包含模型版本和置信信息，便于重放与问题定位。

### S5：本机模型网关与完整日报闭环

**任务**

1. 用 Microsoft.Extensions.AI 注册 IChatClient 与 IEmbeddingGenerator，接入 Ollama/LM Studio。
2. 自定义装饰器只实现：档位路由、脱敏、审计、能力探测、超时与回退。
3. 建立模型基准集与评分表：中文摘要质量、语义标签、JSON 合规、耗时、内存/显存。
4. 评测本机候选对话模型（默认 `gemma4:12b`），确定轻量与质量配置，不在代码中写死模型 tag。
5. 默认嵌入使用 `embeddinggemma-2:740m`；实现向量写入、重建和版本元数据。
6. 实现活动分段、map-reduce、结构化 schema 校验、有限重试与失败保留。
7. 实现持久 report_jobs、单消费者、三通道触发、版本化日报、启动补偿和进度通知。
8. 完成日报面板、站会视图、手动重生成与导出 Markdown。

**验收**

- 手动覆盖与启动补偿语义不同且幂等；重复触发不会生成无意义的重复任务。
- 在生成过程中强制结束进程，重启后任务可恢复或明确失败，不留下"看似成功"的空日报。
- 模型不可用、输出非法 JSON、上下文超限均有可恢复路径。
- 日报记录模型、档位、脱敏状态和证据引用；算法失败不影响原始事件。

### S6：公司内网、公网档与企业策略

**任务**

1. 实现 models.json、本机/内网/公网端点配置和配置版本迁移。
2. 支持 API Key、mTLS、超时、代理和企业 CA；敏感配置进入系统密钥库。
3. 启动时探测 chat、streaming、embeddings、structured output、tool calling，缓存并展示结果。
4. 实现 AllowPublicCloud 等策略锁定，策略由 Host 在最外层强制，不只靠 UI 禁用。
5. 所有外发调用经过统一脱敏与最小化管线；公网首次启用和敏感命令二次确认。
6. 内网健康检查失败时按配置回退本机档；本机也不可用时明确暂停生成，不伪造成功。

**验收**

- 绕过 UI 直接调用用例也无法违反企业策略。
- 审计日志只有时间、档位、模型、Token、结果等元数据，无正文。
- 公网档断网、内网档断连、证书错误、模型不存在均有明确提示和后续补偿。

### S7：本地唤醒与基础语音命令

**任务**

1. 使用 sherpa-onnx 开放词表 KWS 配置"你好时光"及备选词，阈值通过实测确定。
2. 实现显式语音状态机：休眠 → 聆听 → 处理 → 反馈 → 休眠；所有转移可取消、可超时。
3. 唤醒提示音、状态栏三态、8 秒命令窗口、取消命令、连续误唤醒保护。
4. 固定命令用本地规则映射到已有用例：生成日报、暂停/恢复、打开视图。
5. 敏感命令走统一二次确认；TTS 只作反馈，不在后台持续运行。
6. 命令默认只写 voice_commands 文本日志，是否记录音频由用户显式选择。

**验收**

- 断网和未连接内网时仍可唤醒和执行本地固定命令。
- 唤醒前音频不落盘；关闭语音总开关后不再创建音频订阅。
- 固定命令与 UI 操作产生同样的业务结果，不复制业务逻辑。
- 形成唤醒率、误唤醒率、端到端延迟和资源占用报告，再决定是否需要备选引擎。

### S8：语义检索、证据问答与完整语音对话

**任务**

1. 实现时间过滤 + 向量召回 + 必要的关键词混合检索，保留统一证据引用。
2. 问答结果必须附可验证证据，点击跳转到时间线对应位置。
3. 语音开放问答只调用同一查询用例，再增加语音输入输出，不另建回答逻辑。
4. 实现周报、统计图表、任意日期范围导出。
5. 只读 MCP / 本地 API 默认关闭；启用时使用 loopback、本地凭据、最小权限和速率限制。
6. 对查询无证据、证据过期、原始媒体已清理等情况明确降级回答。

**验收**

- 回答中的每个关键事实均能追溯到结构化事件或仍存在的媒体证据。
- 原始媒体到期清理后，问答不会生成无法验证的"截图证据"链接。
- MCP 未授权、越权写入和非 loopback 访问均被拒绝。

### S9：发布加固与持续运维

**任务**

1. Windows MSIX/自包含 exe 签名；macOS 主应用与 helper 签名公证；Linux AppImage/deb 等按 RID 验证。
2. 执行 24 小时与多日浸泡测试，覆盖待机、活跃、休眠唤醒、插拔显示器和麦克风。
3. 完成磁盘满、数据库损坏、密钥不可用、模型离线、helper 崩溃的故障注入。
4. 完成升级迁移、降级策略、备份恢复、卸载数据处理说明。
5. 完成无障碍、键盘导航、权限引导、数据删除与导出的最终检查。
6. 发布性能报告、隐私说明、已知限制、SBOM 和回滚包。

**发布门槛**

- 无阻断级数据丢失、明文敏感数据落盘或绕过策略的问题。
- 安装、升级、卸载、备份恢复均有实测记录。
- 文档明确 Wayland、平台权限、原始媒体保留期和模型资源要求。

---

## 6. 测试策略

| 测试层 | 重点 | 自动化方式 |
|---|---|---|
| 单元测试 | 领域规则、时间边界、状态机、分段算法、脱敏 | 每个 PR，使用 FakeClock/Fake 端口 |
| 架构测试 | 项目依赖方向、禁用跨层调用、公共契约稳定性 | 每个 PR，失败即阻断 |
| 适配器契约测试 | 三平台采集、密钥库、媒体库、模型 Provider | 同一测试套件运行所有实现 |
| 数据库集成测试 | 加密、事务、迁移、备份恢复、向量重建 | 临时隔离环境，不使用开发者真实数据 |
| 黄金集评测 | OCR、中文 ASR/KWS、日报质量、结构化输出 | 固定样本与评分基线，版本间对比 |
| 故障注入 | 进程退出、断电等价场景、断网、权限撤销、磁盘满 | 自动 + 平台实验室 |
| 安全测试 | 密钥/正文日志泄露、loopback 鉴权、策略绕过、明文媒体 | 每个发布候选版本 |
| 性能与浸泡 | 待机/活跃 CPU、内存、磁盘、句柄、队列积压 | 定期与发布前 |
| UI/平台人工测试 | 权限引导、Wayland 回退、托盘、签名安装 | 按能力矩阵逐项签字 |

测试数据使用合成或明确授权的数据，禁止把开发者真实个人日报直接提交到仓库。

---

## 7. CI/CD 与工程纪律

### 7.1 每个 PR 必须通过

- 构建、单元测试、架构测试、格式与静态分析。
- 涉及端口的变更必须运行对应契约测试。
- 涉及 Schema 的变更必须包含迁移、回滚/恢复说明和测试。
- 涉及隐私边界的变更必须更新威胁模型和验收用例。
- 原生依赖变更必须触发三平台打包验证。

### 7.2 分支与发布策略

- 使用小步 PR 和特性开关，不在长期分支中一次性重写平台层。
- 默认配置始终安全；实验能力通过配置开启，Wayland 默认路径保留可诊断回退。
- 发布包带应用版本、Schema 版本、IPC 协议版本、模型配置版本。
- 数据升级默认单向迁移；回滚应用前先备份，禁止旧版本直接写入新 Schema。

### 7.3 可观测性

- 使用结构化日志、OpenTelemetry 和本地诊断导出。
- 指标包括队列深度、丢弃/合并帧数、任务延迟、模型耗时、helper 重启次数、能力状态。
- 默认不采集正文、URL 全路径、音频文本、密钥或完整文件路径到遥测。
- 诊断导出前由用户预览并显式确认。

---

## 8. 主要风险与回退设计

| 风险 | 早期信号 | 缓解与回退 |
|---|---|---|
| SQLite3MC + sqlite-vec 原生组合失败 | 某 RID 无法加载、备份后无法恢复 | S1 阻断；IVectorIndex 替换实现，不降低数据库/媒体加密 |
| 中文 KWS 误唤醒或漏唤醒 | 黄金集和真实环境差异大 | 调整逐词阈值；保留 openWakeWord/Porcupine 备选适配器 |
| macOS helper 不稳定 | 权限撤销后崩溃、IPC 超时 | 监督重启、协议版本化；只降级采集，不影响历史浏览 |
| 原生 Wayland 实验性缺陷 | 特定桌面启动/渲染失败 | 自动或手动回退 X11/XWayland，保留诊断信息 |
| 模型结构化输出不稳定 | JSON 校验失败、标签漂移 | Schema 校验、有限重试、保守模板；更换模型配置不改用例 |
| 常驻资源超标 | 队列积压、句柄增长、待机 CPU 高 | 有界队列、按需加载模型、减少抓帧；资源预算纳入 CI 基线 |
| 公网脱敏不完整 | 预览发现敏感内容 | 默认不发原文，只发最小摘要；公网档可全局禁用 |
| 数据迁移失败 | 升级测试中断或库损坏 | 停止写入、保留原库与备份、进入恢复流程，禁止强行启动新 Schema |

---

## 9. 必须维护的 ADR

1. 采用模块化单体与端口适配器，而非微服务。
2. Application/Host 边界与依赖方向。
3. SQLite3MC、系统密钥库与媒体 AES-GCM。
4. sqlite-vec 的隔离、版本与索引重建策略。
5. macOS Swift helper 与 IPC 协议。
6. 原生 Wayland 默认、X11/XWayland 回退及能力降级。
7. sherpa-onnx 统一语音栈与 AudioHub 单路分发。
8. Microsoft.Extensions.AI 与三档策略装饰器。
9. UTC + 本地日期 + 时区的日报归档规则。
10. 任务幂等、启动补偿与日报版本策略。

ADR 只记录会影响后续替换、数据兼容、安全边界或运维的决策，不记录琐碎实现。

---

## 10. 首个可执行迭代清单

按以下顺序启动，避免一开始同时引入所有风险：

- [x] 创建解决方案、依赖约束、CI 和 ADR 骨架。
- [x] 定义 ActivityEvent、时间模型、CapabilityState 与首批端口。
- [x] 完成 SQLite3MC 加密数据库的最小读写与错误密钥测试。
- [x] 完成 sqlite-vec 联合加载、备份恢复和原生 RID 打包 POC。
- [x] 实现系统密钥库与 AES-GCM 媒体存储的最小闭环。
- [x] 实现 Windows 窗口事件采集和单条事件落库。
- [x] 实现有界采集管道、暂停、黑名单与重复帧合并。
- [x] 实现 Avalonia 时间线，读取加密事件与媒体。
- [x] 完成异常退出、权限拒绝、磁盘满三个故障测试。
- [x] 通过 S1/S2 验收后，再启动 macOS helper 与 Linux Wayland 工作。
- [x] S3.1：完成 macOS 版本化 AF_UNIX IPC、一次性 Token 鉴权、心跳超时、崩溃退避监督器、Swift Helper 原生适配器、LaunchAgent 与 Windows 契约/故障测试。
- [x] S3.2：完成 Linux 原生 Wayland 默认、X11 回退、portal 授权恢复、AT-SPI / EWMH、inotify 与 XDG 自启适配器及 Windows 契约测试。
- [x] 三平台 CI 完成构建、53 项测试、Swift 6 Helper 编译/冒烟/打包及 Linux Portal 打包验证（运行 `44703620747`）。
- [x] S4.1：可组合 `IProcessingStage`、阶段超时/故障隔离、文本提取、pHash 去重与本地脱敏流水线，原始事件永不因预处理失败丢失。
- [x] S4.2：单路 `AudioHub`、内存预滚、多订阅广播与最后订阅释放；Windows WASAPI、macOS AVAudioEngine、Linux PulseAudio 工厂接入。
- [x] S4.3：sherpa-onnx 中文 Paraformer ASR、Silero VAD、pyannote 说话人分离适配器与模型缺失降级测试。
- [ ] S4-R：外挂中文语音模型资产完成识别质量黄金集评测；Linux RapidOCR 与 Whisper.net 高质量批处理保留为遗留项。
- [x] S5.1：Microsoft.Extensions.AI 本机网关、Ollama/LM Studio 适配、loopback 策略、超时/脱敏/审计装饰器与能力探测。
- [x] S5.2：事件向量索引、活动分段、map-reduce、结构化 JSON 校验重试、版本化加密日报与站会 Markdown。
- [x] S5.3：单消费者三通道调度、启动补偿、幂等/失败保留、日报/站会面板、强制重生成与 Markdown 导出。
- [ ] S5-R：安装真实候选模型运行固定中文基准集并确定默认轻量/质量档位；多日调度浸泡并入 S9。
- [x] S6.1：版本化 `models.json`、加密端点密钥、mTLS/企业 CA/代理与三档端点装配。
- [x] S6.2：Host 外层策略锁定、公网显式同意、三档路由与内网→本机回退、最小化/脱敏/残留检测三闸。
- [x] S6.3：chat/streaming/embeddings/structured output/tool calling 探测缓存与诊断展示。
- [ ] S6-R：真实企业 mTLS/CA/代理、公网端点和密钥导入流程联调。
- [x] S7.1：sherpa-onnx KWS/TTS 适配、语音端口、模型缺失降级与唤醒提示音。
- [x] S7.2：四态语音状态机、8 秒命令窗口、误唤醒冷却、固定命令、敏感二次确认与加密文本日志。
- [x] S7.3：语音面板与状态栏、Host 单路 AudioHub 装配、Windows/Linux 反馈播放与 macOS 明确降级。
- [ ] S7-R：真实中文 KWS/TTS 黄金集、macOS 音频输出与命令音频 opt-in 落盘。
- [x] S8.1：时间过滤 + 关键词 + 向量 RRF 混合检索、统一 `[E#]` 证据引用，无证据不生成推测性回答。
- [x] S8.2：任意区间/周报统计、每日柱状数据、Markdown + stats JSON 导出，模型不可用时确定性统计降级。
- [x] S8.3：只读 loopback MCP（默认关闭、加密本地令牌、loopback 校验、限流、四个只读工具、无写工具）。
- [x] S8.4：检索/问答面板、证据点击跳转时间线、语音问答复用同一用例、诊断页 MCP 令牌显示与轮换。
- [ ] S8-R：真实 embedding 检索质量评测、真实 MCP 客户端联调与图形化周报导出。
- [x] S9.1：跨平台打包脚本（Windows 自包含 zip + 可选 Authenticode/MSIX、macOS `.app` + codesign/notarize、Linux tar.gz + deb）、应用图标与签名发布工作流。
- [x] S9.2：数据删除服务与 Host 维护命令（backup/restore/purge）、v1→v2 迁移保留数据、密钥丢失与删除后可用性故障注入测试。
- [x] S9.3：数据与导出页面（二次确认删除）、键盘快捷键、关键控件无障碍名称、诊断页权限引导与确认导出。
- [x] S9.4：隐私说明、已知限制、性能报告、升级卸载、回滚手册与发布检查表。
- [x] S9.5：离线 CycloneDX SBOM 生成器（SHA-512/依赖图/清单校验）、打包 `SHA256SUMS`、发布说明模板与 CI/Release 接入。
- [x] S9.6：可配置 3–14 天加速浸泡（含 8 小时休眠窗口）、浸泡报告与 CI 上传。
- [x] S9.7：MCP Streamable HTTP 兼容（JSON/SSE、会话头、版本协商、DELETE）、无头 `--mcp-serve`/`--mcp-token`、10 项协议一致性测试与独立客户端脚本。
- [x] S9.8：`--benchmark-model` 模型基准命令（报告、失败详情、离线退出码）与 loopback 端点绕过系统代理修复。
- [x] S9.9：检索质量基准（关键词-only 开关、Recall@K/MRR 评估器、合成语料同义词测试与报告）。
- [x] S9.10：`--benchmark-retrieval` 真实 embedding 评测、可配置查询指令前缀与 embeddinggemma 默认指令。
- [x] S9.11：embeddinggemma 评测与生产接入（`EmbeddingProfile`、`title/text` 文档格式、`--rebuild-vectors`、索引版本 1.1）。
- [x] S9.12：默认对话模型切换为 `gemma4:12b`，移除代码/测试/文档/示例中的旧模型配置。
- [x] S9.13：模型端点密钥导入（stdin/诊断页）、列表/删除命令与 `--probe-model` 能力探测。
- [x] S9.14：周报 SVG 图形化导出（每日柱状、统计副标题、XML 转义、随 Markdown/JSON 一并写出）。
- [x] S9.15：14 天等效加速浸泡通过（674/674 控制事件、336 媒体、休眠窗口零事件、保留清理 336/336）。
- [x] S9.16：`--help` / `--version` / `--doctor` 运维自检命令（存储健康、模型探测、MCP 状态）。
- [x] S9.17：无头 `--report-range`（Markdown/SVG/stats）与 `--ask`（证据问答、无证据退出码 1）命令。
- [x] S9.18：周报 PNG/PDF 导出（`AiAssistant.Export` + SkiaSharp，PDF 字形轮廓避免字体嵌入）。
- [x] S9.19：可检索 PDF（PDFsharp + 系统 TrueType 中文字体子集，无字体时回退矢量轮廓）。
- [x] S9.20：PDF 字体自动发现（扫描系统字体并用 SkiaSharp 校验 CJK 覆盖）与 `--doctor` 字体输出。
- [x] S9.21：MSIX 自签名打包/签名/注册/启动/卸载演练；关闭 AppData 虚拟化，卸载保留数据；脚本支持 `-SkipTimestamp`/`-VerifyRootPath`。
- [ ] S9-R：真实证书签名/公证、真实时间多日浸泡与设备热插拔、真机安装升级卸载与无障碍人工验收、CI 实际生成并随 Release 发布 SBOM/校验和。
- [ ] 在真实 macOS 桌面完成 TCC 权限与签名公证验收；在真实 Linux 桌面完成 Wayland / X11 能力矩阵验收。

---

## 11. 完成定义（Definition of Done）

一个功能只有同时满足以下条件才算完成：

1. 用户可观察行为与 DESIGN.md 一致，平台降级已明确呈现。
2. 依赖方向、接口契约和资源释放符合本文约束。
3. 单元/集成/契约测试齐全，关键故障路径已验证。
4. 默认配置不扩大采集范围、不新增隐式外发、不降低加密要求。
5. 日志、指标、错误提示可诊断，但不泄露敏感正文。
6. 有迁移、回滚或替代路径；不能只提供理想环境演示。
7. 相关 ADR、能力矩阵和测试基线已同步更新。
