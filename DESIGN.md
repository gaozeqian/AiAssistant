# AI 个人活动追踪助手 · 设计方案

> 全天候采集电脑上的操作行为（屏幕、文件、麦克风、窗口/网页），结构化存储到本地，
> 再由大模型按天聚合成可读的「数字日记」。
>
> 版本：v2.5 ｜ 日期：2026-11-19 ｜ 目标平台：Windows 10/11 · macOS (Apple Silicon) · Linux
>
> **v2.0 变更**：① 技术栈改为 .NET 10 后端 + Avalonia 12 前端；② 日报层新增手动触发 +
> 关机补偿；③ 从 Windows 单平台扩展为三平台。
>
> **v2.1 变更（竞品驱动优化）**：调研 Screenpipe / Dayflow / MeMex 等同类产品后，
> ① 采集改为事件驱动 + 无障碍树优先；② 日报增加语义粒度并新增站会/周报视图；
> ③ P3 增加可追溯问答与只读 MCP；④ 强化数据可迁移性。竞品详情见附录 A。
>
> **v2.2 变更（模型后端分档）**：模型默认本机离线；新增**公司内网部署**档（数据不出内网，
> 适配办公环境）与公网 API 档（显式开启、强制脱敏）。通过统一模型网关按端点配置切换，
> 采集/存储/摘要算法不变。详见第 8 章。
>
> **v2.3 变更（语音控制）**：新增语音唤醒 + 语音指令，默认唤醒词 **"你好时光"**（备选见 14.2，
> 可自定义）；唤醒检测**始终本机离线**、音频不落盘，唤醒后的对话/问答才按三档模型配置处理。
> 目标为完整语音对话（含带证据回链的自然语言问答）。详见第 14 章。
>
> **v2.4 变更（技术选型评审修正）**：① 数据库加密从已弃用的 `bundle_e_sqlcipher` 迁移到
> SQLite3 Multiple Ciphers，补充系统密钥库与媒体文件加密；② 语音统一为 sherpa-onnx 优先，
> 中文唤醒词免训练，Whisper.net 作高质量 ASR 备选；③ 模型网关复用 Microsoft.Extensions.AI；
> ④ macOS 原生能力采用 Swift helper，明确 Avalonia/Wayland 的能力边界；⑤ 更新 LLM/嵌入模型选型，
> 并加入加密 + 向量扩展、中文语音、平台能力的 POC 验证门槛。详见附录 E。
>
> **v2.5 变更（Linux 默认路径）**：Avalonia 桌面 UI 改为**原生 Wayland 默认**，
> X11/XWayland 作为兼容回退；Wayland 后端仍标注实验性风险，抓屏、前台窗口和全局热键的
> 系统限制不变，继续采用 portal 授权与能力降级。详见附录 F。

---

## 1. 项目概述

### 1.1 定位

这是一个**本地优先（local-first）的个人活动记忆系统**，思路类似 Rewind / Limitless：
采集 → 结构化 → 本地存储 → 按天汇总成人能读的日报。**默认所有推理在本机完成，数据不出本机**；
在办公环境可切换为公司内网部署的模型（数据仍不出内网）；公网 API 作为需显式开启的最后选项。

### 1.2 设计原则

1. **本地优先，边界可控**——采集与存储默认离线；模型默认本机；内网/公网调用都需显式配置，
   且 UI 始终标明当前档位与数据流向（v2.2 将"数据留在本地"细化为三档数据边界）。
2. **用户随时可控**——全局暂停开关、应用黑名单、单条删除、清空某天，以及语音唤醒总开关。
3. **采集边界清晰**——不记录键盘输入、不读剪贴板全文，靠屏幕 OCR + 窗口标题还原活动。
4. **数据可迁移**（v2.1 新增）——标准 SQLite + Markdown 导出 + 本地 API，不锁定任何厂商或模型。
   这是从赛道鼻祖 Rewind 被收购关停的教训中得出的：绑定单一厂商的云端记忆产品极其脆弱。
5. **模型可替换**（v2.2 新增，v2.4 修正）——基于 Microsoft.Extensions.AI 标准抽象 + OpenAI 兼容协议，
   本机/内网/公网后端可随时切换，切换只动配置、不动代码。
6. **唤醒离线，语音可控**（v2.3 新增）——唤醒词检测常驻本机、不随三档模型切换；
   唤醒前音频不落盘，唤醒后由用户显式发起才进入对话。

---

## 2. 技术选型与项目结构

### 2.1 技术栈

| 层 | 选型 | 说明 |
|---|---|---|
| 后端 / 服务 | **.NET 10 LTS**（C#） | 稳定支持至 2028；不追刚发布的 .NET 11 STS |
| 桌面 UI | **Avalonia 12.1+** | **原生 Wayland 默认**（标注实验性风险）；X11/XWayland 兼容回退 |
| 数据库 | Microsoft.Data.Sqlite.Core + **SQLite3 Multiple Ciphers** | `SQLite3MC.PCLRaw.bundle`；商业 SQLCipher/SEE 可选 |
| 向量检索 | sqlite-vec（接口隔离） | 个人规模无需独立向量库；pre-v1，先做加密组合 POC |
| 推理 | **Microsoft.Extensions.AI** + OpenAI 兼容 Provider | 官方 `IChatClient` / `IEmbeddingGenerator` + 自定义策略层 |
| 本地语音 | **sherpa-onnx（C#）** + Whisper.net 备选 | KWS / ASR / VAD / 说话人分离 / TTS 统一技术栈 |
| 语音唤醒 | sherpa-onnx 开放词表 KWS（本机常驻） | 中文自定义免训练，命中后才启动完整 ASR |
| 语音合成 | sherpa-onnx TTS / 平台系统 TTS | 默认本机，短提示可只用提示音 |

> Avalonia 是 .NET 原生跨平台 UI 框架，"前后端"同处一个 .NET 解决方案，通过 MVVM 解耦，
> 无需跨语言通信。

### 2.2 解决方案结构

```
AiAssistant.sln
├── AiAssistant.Core            # 领域模型、接口抽象（IScreenCapture / IAudioCapture / ...）
├── AiAssistant.Capture         # 采集编排（调用平台实现）
├── AiAssistant.Processing      # OCR / ASR / VAD / 脱敏 / 说话人分离
├── AiAssistant.Storage         # SQLite3MC 加密 / sqlite-vec / 生命周期
├── AiAssistant.Models          # Microsoft.Extensions.AI + 档位路由 + 脱敏/审计/能力探测 (v2.4)
├── AiAssistant.Voice           # sherpa-onnx KWS/ASR/TTS + 语音会话状态机 (v2.4)
├── AiAssistant.Summarization   # map-reduce 日报（只依赖标准模型接口）
├── AiAssistant.Scheduler       # 三通道触发 + 任务队列 + 补偿
├── AiAssistant.Desktop         # Avalonia 12.1+ UI（MVVM）
├── AiAssistant.Platform.Windows  # Windows 平台实现
├── AiAssistant.Platform.MacOS    # .NET IPC 客户端，对接 Swift 原生 helper (v2.4)
├── AiAssistant.Platform.Linux    # Wayland/X11 + AT-SPI + Tmds.DBus portal / PipeWire
└── native/AiAssistant.MacHelper  # Swift：ScreenCaptureKit/Vision/CoreAudio/AX/TTS (v2.4)
```

平台实现通过依赖注入在启动时注册；核心层只依赖接口，不感知具体 OS。

### 2.3 总体数据流

```
① 采集层   屏幕截图 · 文件监控 · 麦克风 · 窗口/网页 · 语音唤醒(v2.3)
      ↓
② 预处理层  OCR 文字 · ASR 转写 · 去重压缩 · 脱敏 · 语音意图识别(v2.3)
      ↓
③ 存储层    加密事件库 · 原始媒体 · 向量索引 · 生命周期 · 语音命令记录(v2.3)
      ↓
④ 汇总层    时间线聚合 · 活动分类 · LLM 生成日报（三通道触发）
      ↓
⑤ 展示层    Avalonia 时间线回溯 · 语义搜索 · 每日简报 · 语音对话面板(v2.3)

横切：本地加密 · 授权/暂停开关 · 敏感信息屏蔽 · 数据留存策略 · 唤醒音频不落盘(v2.3)
```

---

## 3. 跨平台设计（核心）

### 3.1 平台能力对照表

| 能力 | 统一接口 | Windows | macOS (Apple Silicon) | Linux |
|---|---|---|---|---|
| 屏幕抓取 | `IScreenCapture` | Windows.Graphics.Capture (WinRT)，回退 GDI `BitBlt` | ScreenCaptureKit，经 Swift helper | **Wayland 默认走 xdg-desktop-portal**；X11 回退 `XGetImage` |
| 多显示器 | 同上 | 原生支持 | 原生支持 | Wayland portal；X11 RandR 回退 |
| 麦克风 | `IAudioCapture` | WASAPI（NAudio） | CoreAudio / AVAudioEngine，经 Swift helper | PipeWire / PulseAudio |
| 前台窗口/应用 | `IWindowActivity` | user32 `GetForegroundWindow`/`GetWindowText` | NSWorkspace + AX，经 Swift helper | Wayland 不保证可用；X11 EWMH 回退 |
| 文件监控 | `IFileMonitor` | ReadDirectoryChangesW；USN Journal 仅作可选增强 | FSEvents，经 Swift helper | inotify；注意 watch 数上限 |
| OCR | `IOcr` | Windows.Media.Ocr（系统自带） | Vision，经 Swift helper | **RapidOCR (ONNX)**，Tesseract 备选 |
| ASR | `IAsr` | sherpa-onnx（流式/离线）；Whisper.net 高质量批处理备选 | 同左 | 同左 |
| VAD | `IVad` | sherpa-onnx + silero-vad | 同左 | 同左 |
| 语音唤醒 | `IWakeWordDetector` | sherpa-onnx 开放词表 KWS | 同左 | 同左 |
| 语音反馈 | `ITts` | sherpa-onnx / 系统 TTS | sherpa-onnx / AVSpeechSynthesizer | sherpa-onnx / espeak-ng |
| 浏览器 URL | 浏览器扩展 | Native Messaging + 本地认证 | 同左 | 同左 |
| 托盘/暂停 | `ITrayApp` | Avalonia TrayIcon + 平台热键 | 同左 | 托盘需 StatusNotifierItem/AppIndicator；Wayland 全局热键降级 |
| 开机自启 | `IAutoStart` | 注册表 Run / 启动文件夹 | launchd LaunchAgent | systemd user / XDG autostart |

**注释**：
- **语音栈统一（v2.4）**：KWS / ASR / VAD / 说话人分离优先共用 sherpa-onnx，减少多框架原生依赖；
  OCR 仍优先系统能力，Linux 用 RapidOCR 保证中文效果。
- **macOS 原生 helper（v2.4）**：ScreenCaptureKit / Vision / CoreAudio / AX 等 Objective-C/Swift 框架
  由 Swift 辅助进程实现，经本地 IPC 与 .NET 主程序通信，避免大规模 P/Invoke 和异步生命周期问题。
- **无障碍树优先（v2.1）**：文字提取首选各平台的无障碍/辅助功能树（Windows UI Automation、
  macOS Accessibility API、Linux AT-SPI），它直接给出结构化的按钮/标签/文本，比 OCR 更快更准；
  **OCR 仅作兜底**（无障碍树不可用或内容非文本时）。这也是 Screenpipe 的核心做法。
- **Wayland 默认与能力边界（v2.5）**：Linux UI 显式引用 `Avalonia.Wayland` 并调用 `UseWayland()`，
  **原生 Wayland 为默认路径**；初始化失败或用户强制兼容模式时回退 X11/XWayland。
  该后端在 Avalonia 12.1 仍标为实验性，必须持续跟踪稳定性；它只解决 UI 渲染，不会解除抓屏、
  前台窗口、全局输入事件和全局热键限制。Wayland 下通过 portal 授权抓屏，不支持的能力明确降级。
- macOS 需申请**屏幕录制**、**麦克风**、**辅助功能**权限，首次启动要有引导页并跳转系统设置；
  Swift helper 必须随主应用一起签名、公证和进行权限声明。

### 3.2 采集策略（各平台统一）— v2.1 改为事件驱动

借鉴 Screenpipe：不再对所有帧无差别处理，而是**只在有意义的变化发生时才抓取/分析**，
目标是把后台占用压到 **<1% CPU / <400MB 内存**，让全天候采集真正无感。

| 数据源 | 关键设计 |
|---|---|
| 屏幕（v2.1 重构） | **事件驱动**：优先监听窗口/焦点/无障碍树变化，避免依赖全局键盘记录；仅在支持的平台监听点击、滚动、键入停顿。抓帧后先取无障碍树，缺失时才 OCR；pHash 判重。10 秒定时兜底；Wayland 等受限环境自动退化为定时抓帧 + 可用元数据 |
| 文件 | **默认只监控用户白名单目录**，不全盘监听；只记录路径/操作/时间，仅对代码/文本类文件读内容，单文件 ≤200KB；USN Journal 仅在用户明确开启且权限允许时启用 |
| 麦克风 | 静音不落盘（VAD 检测人声才切分音频块）；配状态栏提示灯；说话人分离；**与唤醒检测共用同一音频流**（见 14.7） |
| 窗口/应用 | 记录应用名和标题 + 无障碍树的结构化上下文（比纯窗口标题信息量大得多） |
| 网页 | 浏览器扩展通过 **Native Messaging + 本地认证**回传当前标签页 URL/标题，不开放无鉴权的 localhost 写入接口 |

**明确不做**：不记录键盘输入（会泄露密码/验证码），不读取剪贴板全文。

---

## 4. 存储层

### 4.1 数据表

```sql
events(id, ts_start, ts_end, type, app, title, summary, content_ref, embedding)
screenshots(id, ts, path, phash, ocr_text)        -- 原始图 WebP 压缩
audio_chunks(id, ts, path, transcript, speaker)   -- Opus 压缩
files(id, ts, path, op, diff_summary)
daily_reports(id, date, markdown, stats_json, model, version,
              generated_at, trigger_source, model_endpoint_type,
              redaction_applied)                  -- v2.2: 记录档位与脱敏，便于追溯
report_jobs(id, target_date, trigger_source, status, attempts,
            error, created_at, finished_at)       -- 三通道任务队列
voice_commands(id, ts, wake_word, transcript, intent, params_json,
               endpoint_type, response_ref)      -- v2.3 语音命令日志（仅文本，不含音频）
```

- `daily_reports.date` 建唯一索引配合 `version`，保证同一天可保留多版本、取 version 最大者为当前。
- `report_jobs` 持久化，是关机补偿能落地的关键——任务不落内存，重启后仍可恢复。
- **数据库加密（v2.4 修正）**：默认 `Microsoft.Data.Sqlite.Core + SQLite3MC.PCLRaw.bundle`；
  不再使用已进入弃用路径的 `SQLitePCLRaw.bundle_e_sqlcipher`。企业需要商业支持时可选官方 SQLCipher / SEE。
- **密钥管理**：Windows DPAPI、macOS Keychain、Linux libsecret；密钥不硬编码、不写入配置文件或日报。
- **媒体加密**：数据库外的截图/音频/缩略图也使用 AES-GCM 加密，密钥由系统密钥库保护的主密钥派生；
  时间线读取时按需解密，不在临时目录留下长期明文副本。
- **向量元数据**：记录 `embedding_model / dimensions / model_version`；更换模型或维度时支持重建索引。
  sqlite-vec 与加密 SQLite 的组合先做三平台 POC，业务层通过接口隔离，必要时可替换实现。
- 生命周期：原始截图/音频**保留 30 天后自动清理**，结构化事件与日报长期留存。

---

## 5. 预处理层

- **OCR**：Windows/macOS 调系统能力，Linux 默认 RapidOCR (ONNX)，统一输出 `文字 + 置信度 + 位置`。
- **ASR（v2.4 修正）**：sherpa-onnx 负责中文流式命令与常规离线转写（可按评测选 SenseVoice /
  Paraformer / Whisper 等模型）；Whisper.net（whisper.cpp）作为高质量批处理备选。
  不再使用“faster-whisper（ONNX）”这一不准确组合，避免引入 Python sidecar。
- **说话人分离**：优先复用 sherpa-onnx 的分段 + speaker embedding 流程，作为后台批处理而非实时常驻。
- **脱敏**：进入模型前用正则 + NER 抹掉卡号、密码、token、手机号——此步骤必须在所有调用之前；
  公网档同时执行“最小必要内容发送 + 发送前预览”，不把正则/NER 视为绝对安全保证。

---

## 6. 汇总层：三通道触发 + 关机补偿

### 6.1 问题与目标

原方案只在 23:30 定时触发一次。若那时电脑关机，当天日报就永久缺失。
新设计要求：**任何有数据的日期，最终都能生成日报**，不依赖某个时刻电脑恰好开机。

### 6.2 三通道

| 通道 | 触发时机 | 行为 |
|---|---|---|
| ① 手动触发 | 用户在 UI 选日期点击生成 | 立即入队；可选择强制覆盖已有日报（生成新 version） |
| ② 启动补偿 | 应用启动时 | 扫描「有事件数据但无日报」的日期，逐个入队补生成；空日期跳过 |
| ③ 运行时定时 | 在线且到 23:30 | 尽力触发当天日报；错过也无妨，交给通道②兜底 |

### 6.3 任务队列与幂等

- 三路触发都写入 `report_jobs`，由单一消费 Worker 串行处理，避免并发抢资源。
- 幂等：处理前检查该 `target_date` 是否已有日报——
  - 手动触发且选择覆盖 → 生成新 version；
  - 启动补偿 → 已存在则直接标记任务 done，跳过。
- 失败处理：`attempts` 计数，指数退避重试（最多 3 次），仍失败则保留任务并提示用户。
- 进度反馈：Worker 通过事件通知 Avalonia UI 显示生成进度/状态。
- 跨天边界：以**本地时区**定义「天」；跨时区旅行时按系统当前时区归档。

### 6.4 分层摘要 map-reduce（生成算法不变）

一天采集下来的 OCR 文字和转写可能达 **10 万+ token**，直接塞给本地模型会爆上下文，
因此采用层级归并：

```
当日原始事件 (10万+ token)
   ↓ 按时间切成 ≤8K token 的分片（上午/下午/晚间…）
分片 A / B / C …
   ↓ 逐片摘要 (map)
分片摘要集合
   ↓ 若仍超 8K 则递归归并一轮
结构化日报 (reduce)
```

输出固定模板（v2.1 增强）：**时间线回顾 · 关键产出 · 待办提取 · 时长统计 · 意义标签**。

**v2.1 语义粒度升级**（借鉴 Dayflow 的"上下文感知"）：活动分段不再止于"打开某应用"，
而是让 LLM 判断**你实际在做什么**——例如同样 2 小时的 Cursor，要区分"交付功能 / 调试鉴权 /
评审 PR / 卡在环境配置"。每个活动段输出：`应用 + 主题 + 语义标签(如 开发/研究/会议/分心) + 简述`。
这是本方案区别于纯时间追踪器的核心质量点。

**派生的回顾视图**（复用同一份时间线数据，低成本扩展）：
- **每日站会视图**：昨日亮点 / 今日重点 / 障碍三项，直接可粘贴到 standup。
- **周报视图**：专注时段、时间去向、主导应用、分心统计（P3）。

---

## 7. 展示层（Avalonia 12.1+）

- MVVM 架构，主窗口含：时间线回看、日报视图、站会视图、设置、权限引导。
- **时间线**：像视频进度条拖动，回看某时刻屏幕缩略图 + 当时 OCR/转写/文件变更。
- **日报面板**：日期列表标注「已生成 / 缺失」；选中日期可手动生成或重新生成；显示进度。
- **站会视图**（v2.1 新增）：昨日亮点 / 今日重点 / 障碍的卡片式呈现，一键复制。
- **周报视图**（v2.1，P3）：专注趋势、时间去向、分心统计与交互图。
- **对话式查询**（v2.1，P3）：自然语言问"上周三我在忙什么"，答案**回链到具体截图/转写作为证据**（借鉴 MeMex/Screenpipe），可点击跳转到对应时间线位置。
- **报告中心**：集中查看所有日报，支持**任意日期范围导出 Markdown**。
- **设置**：采集开关、事件灵敏度、麦克风开关、应用/网站黑名单、数据保留天数、开机自启。
- **模型配置**（v2.2，v2.4 增强）：三档切换、端点 URL、API Key、模型名、TLS、连接测试；
  启动时探测 chat / streaming / embeddings / structured output 等能力，UI 显示不支持项；
  当前档位与数据流向在状态栏**常驻显示**；公网档启用需二次确认。
- **企业策略指示**（v2.2）：若 IT 下发了"仅内网"锁定配置，公网选项显示为禁用并说明原因。
- **语音控制**（v2.3，v2.4 修正）：状态栏常驻「休眠 / 聆听中 / 处理中」三态与唤醒指示灯；
  设置里编辑唤醒词（默认"你好时光"，sherpa-onnx 开放词表免训练）、逐词阈值、TTS 开关、
  是否把语音命令写入活动记录；语音对话面板展示实时转写、回答、证据回链与命令历史。
- **托盘图标**：常驻托盘，右键菜单含「暂停/恢复采集」「暂停/恢复语音唤醒」「生成昨日日报」「退出」；
  全局热键（如 Ctrl+Alt+P）暂停采集。
- **权限引导页**（尤其 macOS）：引导授予屏幕录制/麦克风/辅助功能权限。

---

## 8. 模型方案：三档后端 + 统一网关（v2.2 引入，v2.4 修正）

### 8.1 设计要点

模型调用全部收敛到 `AiAssistant.Models` 的**统一模型网关**。业务层（摘要、问答、嵌入）依赖
Microsoft.Extensions.AI 的标准接口：`IChatClient` / `IEmbeddingGenerator`，语音场景按需使用
`ISpeechToTextClient` / `ITextToSpeechClient`。自定义层只负责**档位路由、脱敏、企业策略、审计、
能力探测与回退**，不重复实现一套通用 AI 抽象。

三档后端优先使用 OpenAI 兼容协议及现有 Provider 适配（Ollama、vLLM、Xinference、企业网关、
主流云厂商均可接入），切换只改配置（端点 URL / API Key / 模型名 / TLS），不动业务代码。
但“OpenAI 兼容”不代表能力完全一致：启动时必须探测 streaming / embeddings / structured output /
tool calling 等能力，并对不支持项禁用或降级；结构化日报输出要有 schema 校验与重试。

### 8.2 三档对照

| | ① 本机离线（默认） | ② 公司内网（可选） | ③ 公网 API（显式开启） |
|---|---|---|---|
| 典型实现 | Ollama / LM Studio | 内网 vLLM / Xinference / 企业模型网关 | OpenAI / DeepSeek / Azure OpenAI |
| 端点 | `localhost:11434` | `http(s)://内网地址:端口/v1` | `https://api.厂商.com/v1` |
| 数据边界 | 不出本机 | **不出公司内网** | 离开组织 |
| 启用方式 | 开箱即用，零配置 | 填入内网端点 | 二次确认 + 显式开关 |
| 脱敏要求 | 按需 | **建议开启** | **强制开启**，且发前预览 |
| 算力来源 | 本机 GPU/CPU | 内网 GPU 池（本机无需强算力） | 弹性按需 |
| 适用人群 | 个人 / 隐私敏感 | **企业办公环境** | 追求最强质量且接受外发 |
| 离线可用 | ✅ 完全可用 | ❌ 需连内网 | ❌ 需公网 |

### 8.3 公司内网档的关键设计（本次重点）

- **零本机算力门槛**：推理跑在内网 GPU 池，普通办公本也能用大模型；本机只做采集和轻量预处理。
- **配置统一分发**：提供 `models.json` 配置文件，企业 IT 可通过 MDM / 组策略 / 登录脚本下发，
  预置内网端点与模型名，员工开箱即用、无需手工填写。
- **策略锁定**：配置项 `AllowPublicCloud=false` 时，UI 隐藏并禁用公网档，仅允许本机/内网——
  满足企业合规要求。
- **内网身份与审计**：支持内网网关要求的 API Key / mTLS；记录"何时用哪档、发送多少 token"到本地
  审计日志（企业可收集），不包含正文内容。
- **连接健康检测**：启动时探测端点连通性与模型可用性，失败自动回退到本机档（可配置），
  避免内网服务波动导致日报中断。

### 8.4 数据外发的统一防线（跨三档生效）

无论哪一档，发送前都经过同一条管线（在 `AiAssistant.Models` 内，不可绕过）：

```
待发送文本 → 正则/NER 脱敏（卡号/token/手机号/密码）→ 按档位强制级别校验 → 发送
```

- 公网档：脱敏**强制且不可关闭**；首次启用时弹出明确告知"数据将离开本机/内网"，需二次确认。
- 内网档：脱敏默认开启，企业可依内部规定调整。
- 本机档：可按需关闭以保留完整上下文。
- 每份日报的 `daily_reports` 记录 `model_endpoint_type` 与 `redaction_applied`，便于追溯。

### 8.5 各任务的模型分工

| 任务 | 本机档选型 | 内网/公网档 | 说明 |
|---|---|---|---|
| 日报归纳 LLM | **`gemma4:12b`（本机 Ollama），经真实日报语料评测** | 网关上的同等或更强模型 | 不写死模型代际；区分轻量档与质量档 |
| 嵌入 | **`embeddinggemma-2:740m`**（768 维）；bge-m3 备选 | 内网嵌入服务 | 中文/多语言优先；记录维度并支持重建索引 |
| ASR | sherpa-onnx；Whisper.net 作高质量批处理备选 | 可切内网/云端 ASR | 命令短语音优先本机流式，长录音可后台批处理 |
| OCR / VAD | 系统 OCR / RapidOCR；sherpa-onnx + silero-vad | 始终本机 | 无外发必要 |
| 唤醒词检测 | **sherpa-onnx 开放词表 KWS** | **不随档位变化，始终本机** | 中文自定义免训练；openWakeWord 仅作备选 |
| 命令意图识别 | 规则模板优先，歧义走本机小模型 | 可走网关 | 固定命令本地即可，无需外发 |
| 语音反馈 TTS | sherpa-onnx / 平台系统 TTS | 始终本机 | 无外发 |

### 8.6 算力底线（仅针对本机档）

- 8GB 显存建议从 **4B 轻量档或 8B/9B 量化质量档**中选型；LLM、嵌入和 ASR 不应默认同时常驻。
- 纯 CPU（16GB 内存）可跑轻量模型，日报放夜间定时或手动触发，不阻塞使用。
- Apple Silicon 统一内存适合本地量化推理；Whisper.net 可按需启用 CoreML/Metal。
- 模型版本不进架构硬编码：用同一批真实中文日报语料评测候选模型的质量、耗时、
  结构化输出稳定性，再确定默认 tag；配置中允许用户切换。
- **选内网档则无以上本机算力顾虑**——这正是办公环境切换内网模型的核心价值。

---

## 9. 关键参数（默认值，可调）

- 抓帧策略（v2.1，v2.4 修正）：**窗口/焦点/无障碍事件驱动为主** + **10 秒定时兜底**；
  全局输入事件仅在平台允许且不记录内容时使用；Wayland 自动降级。pHash 汉明距离 ≤5 视为同画面丢弃。
- **Linux UI 后端（v2.5）**：原生 Wayland 默认；启动失败回退 X11/XWayland，设置中可强制指定后端。
- 文字提取：**无障碍树优先，OCR 兜底**（v2.1）。
- 音频切分：VAD 静音 **700ms** 断句，单块上限 60 秒。
- 唤醒词（v2.3，v2.4 修正）：默认 **"你好时光"**，备选 "小忆同学" / "你好小记" / "小录"；
  sherpa-onnx 通过关键词配置自定义，无需逐词训练。
- 唤醒引擎：sherpa-onnx KWS；每个关键词分别配置 boosting score 与 trigger threshold，
  默认值由中文实测确定，不沿用其他引擎的 0.6 阈值；命中后开 **8 秒**命令窗口，超时退回休眠。
- 误唤醒防护：命中给提示音 + 指示灯；删除数据 / 公网查询类敏感命令需二次确认。
- 分片大小：默认 **≤8K token** 的保守分片，按当前模型实际上下文动态调整；逐段 map 摘要。
- 归并：段摘要拼起来若还超 8K，再递归归并一轮（两层足够覆盖一天）。
- 定时触发：**每日 23:30**（在线时尽力触发）；启动补偿覆盖离线缺失。
- 失败重试：最多 3 次，指数退避（1min / 5min / 15min）。
- 数据保留：原始媒体 30 天，结构化事件与日报长期。
- 资源目标：采集与 KWS 常驻；ASR 仅在唤醒后或后台批处理启动，LLM 不常驻；<1% CPU 只作为待机目标，
  活跃转写/生成阶段单独测量并展示。

---

## 10. 依赖清单

```
# .NET 基础
.NET 10 SDK (LTS), Avalonia 12.1+, CommunityToolkit.Mvvm (MVVM)

# 采集（平台相关）
Windows: CsWinRT / Windows.Graphics.Capture, NAudio, UIAutomationClient
macOS:   自研 Swift helper + 本地 IPC（ScreenCaptureKit / Vision / CoreAudio / AX / NSWorkspace）
Linux:   Avalonia.Wayland + UseWayland()（默认）, X11/XWayland 回退,
         AT-SPI, Tmds.DBus (portal), PipeWire / PulseAudio

# 预处理与语音（v2.4）
org.k2fsa.sherpa.onnx: KWS / ASR / VAD / speaker diarization / TTS
Whisper.net: 可选高质量 ASR（whisper.cpp，CPU / CUDA / CoreML / Metal）
RapidOCR (ONNX): Linux 中文 OCR；Tesseract 仅作备选
脱敏: 正则 + 本地 NER；公网档增加最小化发送与预览

# 存储（v2.4）
Microsoft.Data.Sqlite.Core, SQLite3MC.PCLRaw.bundle, sqlite-vec
商业可选: 官方 SQLCipher / SQLite SEE
密钥: Windows DPAPI / macOS Keychain / Linux libsecret；媒体文件 AES-GCM

# 推理（v2.4）
抽象: Microsoft.Extensions.AI（IChatClient / IEmbeddingGenerator / 语音接口）
适配: OpenAI 兼容 Provider + OllamaSharp 等现有适配器
本机: Ollama / LM Studio；内网: vLLM / Xinference / 企业网关；公网: 显式开启
策略: 档位路由 + 脱敏 + 审计 + 能力探测 + 回退；models.json + MDM/组策略

# 调度
Microsoft.Extensions.Hosting.BackgroundService + PeriodicTimer
（已有 SQLite 持久任务队列，不引入 Quartz.NET）

# 托盘/热键/浏览器
Avalonia TrayIcon, 平台全局热键（Wayland 降级）, 浏览器 Native Messaging + 本地认证
```

---

## 11. 必须提前验证的工程点

1. **脱敏仍在本地做**：即便不出本机，贴图里若有密码/验证码，摘要时也要正则 + NER 抹掉，避免日报文件本身成为明文敏感库；公网档还需最小化发送与预览。
2. **磁盘与加密**：原始截图+音频一天约 100–300MB。设 30 天自动清理，结构化事件和日报永久保留；数据库外的媒体文件也必须加密。
3. **资源竞争**：采集/KWS 轻量常驻，ASR 只在唤醒后或批处理启动，LLM 不常驻；资源指标分待机与活跃阶段测量。
4. **组合 POC 先行**：先验证 SQLite3MC + sqlite-vec 三平台打包、中文 KWS/ASR 与单路麦克风复用、macOS Swift helper、Wayland 降级，再进入大规模业务开发。

---

## 12. 隐私与合规（务必遵守）

- **录音涉及他人**：在多数地区未经同意录制他人对话可能违法（中国《民法典》隐私权与《个人信息保护法》、美国部分州要求双方同意）。建议默认仅在独处时开启、有明显提示、原始音频不分享、不上传。
- 全局热键暂停（如 `Ctrl+Alt+P`）。
- 应用/网站黑名单（密码管理器、网银、私密窗口）。
- 结构化事件和日报都提供单条删除与「清空某天」。
- 本地模式为默认；内网/公网调用都需要显式配置，并在 UI 里标明数据边界（不出本机 / 不出内网 / 离开组织）。
- **数据外发三道闸（v2.2）**：① 发送前统一脱敏不可绕过；② 公网档启用需二次确认；③ 企业可锁定禁用公网档。
- 每份日报记录所用档位与脱敏状态，可追溯；审计日志只记调用元数据（时间/档位/token 数），不含正文。
- **语音唤醒（v2.3）**：唤醒前音频只在内存环形缓冲（约 2 秒）滚动、**不落盘、不外发**；
  唤醒词检测始终本机，不随三档模型切换；语音命令文本默认不写入活动记录（可在设置开启）；
  删除数据/公网查询类命令需二次确认；唤醒总开关随时可关，关闭后不监听任何音频。
- **存储加密（v2.4）**：数据库使用 SQLite3MC，媒体文件使用 AES-GCM；密钥由 DPAPI / Keychain /
  libsecret 保护。加密是防止离线拷贝泄露，不替代操作系统账户与磁盘安全。
- **本地接口认证**：浏览器 Native Messaging、本地 API 与只读 MCP 均需本地凭据/配对和最小权限；
  网络服务仅绑定 loopback，不开放无鉴权写入。
- macOS 权限需用户显式授予，应用不得绕过；Wayland 下抓屏须经 portal 用户授权。

---

## 13. 打包与分发

| 平台 | 形式 | 注意 |
|---|---|---|
| Windows | MSIX 或自包含 exe | 代码签名避免 SmartScreen 警告；验证 SQLite3MC/sqlite-vec/sherpa-onnx 原生库打包 |
| macOS | .app（arm64），签名 + 公证 | Swift helper 与主应用统一签名/公证；声明屏幕录制/麦克风/辅助功能权限 |
| Linux | AppImage / deb / Flatpak | 按 RID 携带原生依赖；Flatpak 需 portal 权限；托盘/热键按桌面环境降级 |

---

## 14. 语音控制：本地唤醒 + 完整语音对话（v2.3 新增，v2.4 修正）

### 14.1 目标与硬约束

像 Siri / 小爱同学 / 小艺一样：喊一声唤醒词，助手就能执行操作或回答问题。
三条硬约束不可突破：

1. **唤醒检测始终本机离线**——不随三档模型切换，断网、未连内网时依然叫得醒；
2. **唤醒前音频不落盘**——只在内存环形缓冲滚动约 2 秒，命中后才开始处理；
3. **语音不绕过隐私闸门**——脱敏、二次确认、企业策略锁定对语音路径同样生效。

### 14.2 唤醒词选型（默认："你好时光"）

| 候选 | 定位 | 音节 | 说明 |
|---|---|---|---|
| **你好时光**（默认，已选） | 贴合时间线回溯的数字日记 | 4 | 辨识度高、仪式感强；与"你好 Siri"同为问候式 |
| 小忆同学 | 记忆主题，节奏类似"小爱同学" | 4 | 误唤醒率低，偏可爱 |
| 你好小记 | 日记主题 | 4 | 自然好念 |
| 小录 | 记录主题，类似"小艺" | 2 | 简短，但两字词日常误唤醒率略高 |

- 唤醒词做成**关键词配置 + 可替换模型**：默认使用 sherpa-onnx 开放词表 KWS，
  将中文短语转换为关键词 token 配置即可，无需为每个唤醒词单独训练。
- 若后续实测中文误唤醒/漏唤醒不达标，再评估 openWakeWord 自训练模型或商业 Porcupine；
  三者统一藏在 `IWakeWordDetector` 后，不影响语音会话与业务层。
- 命名原则：3–4 音节为佳，避开"打开 / 确定 / 播放"等日常高频短语，避开他人商标。

### 14.3 两段式语音链路

```
待机：16kHz 单声道PCM → 内存环形缓冲(约2s) → 唤醒引擎推理 → 休眠
                                  ↓ 命中"你好时光"
唤醒：提示音 + 状态栏「聆听中」 → 开 8 秒命令窗口（VAD 断句，可续听）
                                  ↓
理解：sherpa-onnx 本机 ASR 转写 → 意图分类 + 槽位填充 → 确定性命令直接执行 / 复杂问答走模型网关
                                  ↓
反馈：UI 卡片展示转写与结果 + TTS 播报简短确认；开放问答附证据卡片
                                  ↓
收尾：8 秒无有效输入或说"取消" → 回休眠；音频缓冲清空
```

- 语音会话是显式状态机（休眠 / 聆听 / 处理 / 反馈），状态在 UI 常驻可见。
- 唤醒与 ASR 分属不同线程/进程，避免大模型推理卡顿影响唤醒响应（呼应第 11 章第 3 点）。

### 14.4 完整语音对话能力（用户选定目标）

| 类别 | 示例语音 | 处理路径 | 模型依赖 |
|---|---|---|---|
| 日报生成 | "生成今天的日报""把昨天的重做一遍" | 意图 → `report_jobs` 入队（复用三通道） | 网关 |
| 活动查询 | "上周三下午我在忙什么" | 时间过滤 + 语义检索 | 网关 + 向量库 |
| 统计问答 | "这周开会开了多久" | `stats_json` + LLM | 网关 |
| 采集控制 | "暂停采集""恢复录音" | 规则直接执行 | 无 |
| 导航 | "打开站会视图""跳到昨天 15 点" | 规则直接执行 | 无 |
| 开放问答 | "总结这个项目最近的变化" | 检索增强问答 | 网关（公网档强制脱敏） |

- 所有回答都附**证据卡片**（点击跳转到时间线对应时刻），延续 P3 的可追溯问答设计。
- "生成日报"类命令与现有任务队列合流，遵守幂等与补偿语义，不会重复生成。

### 14.5 与三档模型网关的关系

| 环节 | 档位关系 | 说明 |
|---|---|---|
| 唤醒词检测 / TTS / 固定命令 | **永远本机** | 企业策略锁定公网也不影响，离线可用 |
| 语音转写 ASR | sherpa-onnx 本机默认 | 内网/公网档可选；Whisper.net 可作高质量批处理备选 |
| 意图识别 | 规则优先，歧义走 `IChatClient` | 固定命令不消耗模型 token |
| 开放问答 / 活动查询 | 走 `IChatClient` / `IEmbeddingGenerator` | 与日报完全相同的档位、脱敏、审计规则 |
| 公网档语音问答 | 三道闸 + 二次确认 | 首次启用公网语音问答时再次明确告知 |

### 14.6 误唤醒与可靠性

- sherpa-onnx 的 boosting score / trigger threshold 按唤醒词分别配置；默认值通过中文实测确定，
  安静/嘈杂环境使用不同配置，连续多次误唤醒自动提高触发阈值。
- 敏感命令（删除数据、清空某天、公网查询）需二次确认：TTS 播报确认语，用户说"确认"或点击按钮。
- 麦克风未插、权限未授予、系统静音时，设置页明确提示"语音控制不可用"，不影响其他功能。
- 语音功能有独立总开关；关闭后不监听任何音频，也不保留环形缓冲。

### 14.7 与麦克风采集链路共存

- **共用一路 PCM 流**：唤醒引擎读 16kHz 单声道低采样副本，ASR 用原始流，
  避免两个消费者争抢麦克风独占（Windows/macOS 上尤需注意）。
- 语音命令**默认不写入** `audio_chunks`，不成为活动记录；只写 `voice_commands` 文本日志。
- 用户显式选择"记录语音命令"时，才把命令段单独落盘，随原始媒体 30 天清理。

---

## 15. 实施路线

| 阶段 | 内容 | 依赖模型 |
|---|---|---|
| **P0** | 解决方案骨架、Core 接口、Windows **事件驱动**采集器 + 无障碍树 + Avalonia 时间线；**POC：SQLite3MC + sqlite-vec + 三平台原生打包** | 否 |
| **P1** | macOS Swift helper / Linux 采集与能力降级；**POC：原生 Wayland 默认 + X11 回退、中文语音与单路麦克风复用**；sherpa-onnx ASR/VAD/说话人分离 + OCR + 判重去噪 | 部分 |
| **P2** | **Microsoft.Extensions.AI 网关 + 本机档(Ollama)** + 模型/嵌入基准评测 + map-reduce 日报 + 站会视图 + 三通道触发/补偿 + 脱敏/黑名单/暂停 | 是 |
| **P2.5**（v2.2） | **内网档 + 公网档**：端点配置、配置分发/策略锁定、能力探测、健康检测与回退、数据外发三道闸、档位状态显示 | 是 |
| **P2.6**（v2.3，v2.4 修正） | **sherpa-onnx 本地唤醒（"你好时光"）+ 基础语音命令** + TTS + 三态指示；实测误唤醒率与资源占用 | 部分 |
| **P3** | 语义搜索、**带证据的问答**、**只读 MCP server**、周报/统计图表、Markdown 范围导出、加密与留存加固；**完整语音对话**（活动/统计/开放问答 + 证据回链） | 是 |

> 建议从 **P0** 起步：先在一个平台（Windows）跑通「事件驱动采集→落库→时间线」，验证数据抓得全不全，
> 再横向扩展到 macOS / Linux。三通道触发在 P2 与日报一起实现。语音控制先做 P2.6 的本地唤醒 +
> 固定命令（不依赖问答能力），完整对话放到 P3 与带证据问答一起交付。

---

## 附录 A：竞品分析（v2.1 新增）

> 基于官方页面、GitHub 仓库、定价页核对，时间 2026-11。部分"对比文章"是厂商软文，已尽量以一手来源为准。

### A.1 同类产品清单

| 产品 | 平台 | 采集 | AI 日报 | 本地 | 开源 | 特点 |
|---|---|---|---|---|---|---|
| **Screenpipe** | Win/Mac/Linux | 屏幕+音频，事件驱动 | 搜索/查询为主 | ✅ | source-available | 无障碍树优先、MCP + Pipes 生态，YC S26 |
| **Dayflow** | 仅 Mac | 屏幕分块 | ✅ 时间线+日/周报 | ✅ | MIT | 上下文感知、站会视图，Swift，7k+ stars |
| **MeMex** | 仅 Mac | 屏幕关键帧 | ✅ 日/周报 | ✅ BYO 模型 | 否 | 带证据的自然语言问答 |
| **Screencap** | 仅 Mac (AS) | 屏幕+OCR | ✅ "Day Wrapped" | ✅ | ✅ | 2026-10 Swift 原生重写，MCP |
| **Goldfish** | 仅 Mac | 屏幕+工作内容 | AI 上下文层 | ✅ | 否(内测) | 给 AI 提供工作上下文 |
| **LUCI** | Win/Mac | 屏幕+会议+邮件 | 供 Agent 读取 | ✅ | 否 | Rewind 替代方向 |
| **Microsoft Recall** | Win11 Copilot+ | 屏幕快照+OCR | 搜索时间线 | ✅ | 否 | 默认关闭，不录音 |
| **ActivityWatch** | Win/Mac/Linux | 应用/窗口标题 | ❌ | ✅ | ✅ | 只做原始数据，无 AI |
| **OpenRecall / Windrecorder** | 多平台 / 仅 Win | 截图历史 | ❌ | ✅ | ✅ | 单纯时间回溯 |

时间追踪类（Timely / Timing / Memtime / Rize / WakaTime）偏工时与账单，非"数字日记"，不构成直接竞争。

### A.2 各家优势提炼

- **Screenpipe**：事件驱动采集 + 无障碍树优先（<1% CPU）；本地 REST API(3030) + MCP server +
  Pipes（markdown 定义的定时 Agent）；应用/窗口/URL 排除、PII 脱敏；三平台 + 多显示器。
- **Dayflow**：上下文感知摘要（区分"研究"与"刷视频"）；每日站会 / 每周回顾视图；
  AI 后端可选（Ollama / LM Studio / Gemini / 本地 CLI）；MIT 许可。
- **MeMex / Screencap**：自然语言问答 + **证据回链**；Screencap 的 MCP 让 Agent 读历史。
- **Rewind 的教训**：2024 更名 Limitless，2025-12 被 Meta 收购并关停采集。
  教训——绑定单一厂商 + 云端的记忆产品脆弱，数据必须可迁移。

### A.3 本方案的差异化定位

| 维度 | 竞品现状 | 本方案 |
|---|---|---|
| 跨平台 | 强 AI 日报产品多为 Mac 独占 | ✅ Win/Mac/Linux 三端全覆盖 |
| 麦克风 | 多数只做屏幕；Screenpipe 是少数例外 | ✅ 屏幕 + 麦克风 + 说话人分离 |
| 触发方式 | 普遍只有自动 | ✅ 手动 + 启动补偿 + 定时三通道 |
| 技术栈 | Electron / Swift / Rust 为主 | .NET 10 + Avalonia（空白点） |
| 可迁移性 | 部分锁定云端 | ✅ 标准 SQLite + Markdown 导出 + 本地 API |
| 模型部署 | 多为单机本地或自带云；少有企业内网档 | ✅ 本机/内网/公网三档，统一网关可按配置切换（v2.2） |
| 语音控制 | 少数有语音，多为固定命令或依赖云端 | ✅ sherpa-onnx 本地唤醒（"你好时光"）+ 完整语音对话 + 证据回链（v2.4） |

> 结论：市场已验证需求真实存在，但"**跨平台三端 + 麦克风 + 完整 AI 日报 + 本地优先 + 企业内网可部署 + 本地唤醒语音对话**"的组合仍是缺口；
> 方案方向成立，重点在 P0 把事件驱动采集做扎实，P2 把语义摘要质量做出来。

---

## 附录 B：v2.1 优化清单（竞品反哺）

| # | 改进 | 来源 | 落地章节 |
|---|---|---|---|
| 1 | 采集改事件驱动 + 无障碍树优先 + OCR 兜底，目标 <1% CPU | Screenpipe | 3.1 / 3.2 / 9 / 10 |
| 2 | 日报增加语义粒度标签；新增站会 / 周报视图 | Dayflow | 6.4 / 7 |
| 3 | 带证据回链的问答 + 只读 MCP server | MeMex / Screenpipe | 7 / 15 (P3) |
| 4 | 标准 SQLite + Markdown 范围导出 + 本地 API，强化可迁移 | Rewind 教训 | 1.2 / 7 / 15 |

## 附录 C：v2.2 变更清单（模型后端分档）

| # | 变更 | 落地章节 |
|---|---|---|
| 1 | 新增三档模型后端（本机离线默认 / 公司内网可选 / 公网显式开启） | 1.1 / 8.2 |
| 2 | 统一模型网关 `IModelProvider`，OpenAI 兼容协议，切换只改配置 | 2.1 / 2.2 / 8.1 |
| 3 | 公司内网档：零本机算力门槛、配置分发、策略锁定、健康检测与回退 | 8.3 |
| 4 | 数据外发三道闸：统一脱敏不可绕过 + 公网二次确认 + 企业锁定 | 8.4 / 12 |
| 5 | 日报记录档位与脱敏状态；模型配置 UI 与状态栏常驻显示 | 4.1 / 7 |
| 6 | 实施路线新增 P2.5；差异化表新增"模型部署"维度 | 15 / 附录 A.3 |

## 附录 D：v2.3 变更清单（语音控制）

> 以下为 v2.3 的历史变更记录；具体引擎与依赖以 v2.4 的第 14 章为准。

| # | 变更 | 落地章节 |
|---|---|---|
| 1 | 默认唤醒词"你好时光"（备选：小忆同学 / 你好小记 / 小录），支持自定义模型导入 | 14.2 / 9 |
| 2 | 唤醒检测始终本机离线、音频仅在内存环形缓冲不落盘，不随三档切换 | 1.2 / 8.5 / 12 / 14.1 |
| 3 | 两段式语音链路 + 会话状态机；误唤醒防护与敏感命令二次确认 | 14.3 / 14.6 |
| 4 | 完整语音对话：日报生成 / 活动查询 / 统计 / 控制 / 导航 / 开放问答，答案带证据回链 | 14.4 |
| 5 | 语音走统一模型网关，公网档同样受三道闸约束 | 14.5 |
| 6 | 与麦克风采集共用一路 PCM，语音命令默认不写入活动记录 | 4.1 / 14.7 |
| 7 | 新增 `AiAssistant.Voice` 项目、`voice_commands` 表、TTS 依赖与设置项 | 2.2 / 4.1 / 7 / 10 |
| 8 | 实施路线新增 P2.6（本地唤醒 + 基础命令），完整对话并入 P3 | 15 |

## 附录 E：v2.4 技术选型修正清单（评审反哺）

| # | 修正 | 原因 / 目标 | 落地章节 |
|---|---|---|---|
| 1 | 保留 .NET 10 LTS；Avalonia 升至 12.1+，Wayland 仅实验性可选（v2.5 已改为默认） | 稳定性优先；UI 支持不等于采集权限 | 2.1 / 3.1 / 13 |
| 2 | `bundle_e_sqlcipher` 改为 SQLite3MC；商业 SQLCipher/SEE 可选 | 原包进入弃用/不维护路径 | 2.1 / 4.1 / 10 |
| 3 | 增加 DPAPI / Keychain / libsecret 与媒体 AES-GCM | 避免只加密数据库、媒体仍明文 | 4.1 / 12 |
| 4 | 语音统一为 sherpa-onnx；中文 KWS 免训练 | C# 三平台预构建，减少多套原生依赖 | 2.1 / 3.1 / 5 / 14 |
| 5 | Whisper.net 作高质量 ASR 备选；删除“faster-whisper(ONNX)” | 修正技术事实并避免 Python sidecar | 3.1 / 5 / 8.5 |
| 6 | 模型网关采用 Microsoft.Extensions.AI + 能力探测 | 复用官方抽象；兼容协议不代表能力一致 | 2.2 / 7 / 8.1 |
| 7 | LLM 默认 gemma4:12b；嵌入默认 embeddinggemma-2:740m | 更新模型代际与中文检索质量 | 8.5 / 8.6 |
| 8 | macOS 使用 Swift helper；Linux 引入 Tmds.DBus | 降低原生框架绑定与异步生命周期风险 | 2.2 / 3.1 / 13 |
| 9 | 文件监控默认目录白名单；浏览器改 Native Messaging + 认证 | 降低权限范围与本地接口滥用风险 | 3.2 / 12 |
| 10 | sqlite-vec 保留但接口隔离；新增组合 POC 与分阶段资源指标 | pre-v1、加密扩展兼容及性能需实测 | 4.1 / 9 / 11 / 15 |

> 主要依据：微软 .NET 支持策略与 Microsoft.Extensions.AI 文档、Avalonia 12.1 官方说明、
> EF Core 11 / SQLitePCLRaw 3.0 迁移说明、sherpa-onnx C# 与开放词表 KWS 官方文档、Ollama 模型库。

## 附录 F：v2.5 变更清单（原生 Wayland 默认）

| # | 变更 | 落地章节 |
|---|---|---|
| 1 | Linux 桌面 UI 改为原生 Wayland 默认，显式使用 `Avalonia.Wayland + UseWayland()` | 2.1 / 3.1 / 9 / 10 |
| 2 | X11/XWayland 调整为初始化失败或用户指定时的兼容回退 | 3.1 / 9 |
| 3 | Wayland 后端仍标注实验性风险；portal 抓授权与窗口/热键能力降级保持不变 | 3.1 / 3.2 / 12 |
| 4 | P1 增加原生 Wayland 默认路径、回退机制和主流桌面环境 POC | 15 |
