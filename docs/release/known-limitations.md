# 已知限制（首版）

> **状态**：living ｜ **最后更新**：2026-11-22 ｜ **适用版本**：0.1.x ｜ **维护触发条件**：每次发布前复核限制是否仍适用

> 与 [../status/backlog.md](../status/backlog.md) 遗留项对应。发布前请逐条确认是否仍适用。

## 平台能力

| 限制 | 影响 | 现状 |
|---|---|---|
| Wayland 连续像素流仅在 GNOME 实测 | KDE/KWin 与多显示器组合未验证；遇到非 RGB 像素格式会回退按需截图 | 已实现 `pw_stream` 消费与降级，可用 `AIASSISTANT_WAYLAND_PIPEWIRE=false` 关闭 |
| macOS TCC 与签名公证 | 屏幕录制/辅助功能授权、Helper 崩溃监督、公证需真机验收 | 真机已验证 GUI 启动、授权弹窗、抓屏→加密落库→报告导出闭环、Helper 强杀重启、未授权降级，及本机 Apple Development 完整签名后采集正常；`spctl` 仍 rejected，正式发布需 Developer ID + 公证 |
| macOS 音频输出 | 已实现 Swift Helper `AVAudioEngine` 播放（`audio.play`，内存 PCM 不落盘）并真机验证；签名 `.app` 内尚未复测 | `S7-R2` 已关闭；发布会话中需在打包应用内复听一次 |
| Linux OCR 需自备模型 | 默认仅 AT-SPI 文本；安装 RapidOCR 模型后才有图像 OCR 兜底，Linux 真实截图集质量未评测（macOS Vision 合成集 CER 0%） | 设置 `AIASSISTANT_RAPIDOCR_MODEL_DIR`；缺模型时 `Ocr` 能力显示 `PermissionRequired`；质量用 `--benchmark-ocr` 评测 |
| Whisper 批处理需自备模型 | 默认 sherpa-onnx；`ggml-small` 真人基线 CER 17.13%（sherpa 6.34%），中文域明显落后；更大模型未测 | 设置 `AIASSISTANT_ASR_ENGINE=whisper` 与 `AIASSISTANT_WHISPER_MODEL_PATH`；质量用 `--benchmark-asr` 评测 |

## 模型与检索

| 限制 | 影响 | 现状 |
|---|---|---|
| 真实模型质量仅部分评测 | gemma4:12b 100%/100%、4.6s 热态；其它候选模型未测 | `S5-R1`，按候选模型分别跑基准后定默认档 |
| 企业 mTLS/CA/代理未联调 | 企业网络可能握手失败 | `S6-R1` |
| 公网端点未联调 | 仅代码级策略与同意门禁 | `S6-R1` |
| 默认模型依赖 Ollama | 未安装 `gemma4:12b` / `embeddinggemma-2:740m` 时对话或向量召回降级 | 安装模型或设置 `AIASSISTANT_CHAT_MODEL` / `AIASSISTANT_EMBEDDING_MODEL`；`--rebuild-vectors` 重建索引 |
| 对话/Embedding 模型需外部准备 | 安装包已含本机语音模型，但 Ollama 与约 8.3GB 的对话/向量模型仍需单独安装或连接内网端点 | 安装 Ollama 并拉取模型，或在诊断页配置内网/公网端点 |
| 检索融合参数未调优 | TopK=8、候选 60、RRF k=60 为初始值；embeddinggemma 实测 95.8%，个别同义词仍漂移 | `S8-R1`，需用真实脱敏语料继续调参 |

## 语音

| 限制 | 影响 | 现状 |
|---|---|---|
| KWS/TTS 未跑真实黄金集 | 合成基线唤醒率 92.3%（score=3.0，13 正）、误唤醒 0.00 次/小时（2.12 小时负样本）、延迟中位数 1100 ms；真人录音与 TTS 客观音质未评测 | 用真人录音复跑 `--benchmark-kws`，再按 score×threshold 网格调优 |
| 可选语音/OCR 资产不随包 | 安装包已含 KWS/ASR/MeloTTS/VAD（约 268MB）；Whisper（466MB）、RapidOCR、说话人分离模型默认不打包 | 需要时用 `stage-bundle.sh --with-whisper/--with-rapidocr` 重新打包，或手工放入 `<数据目录>/models/` |
| 命令音频默认不落盘 | 默认无法回听；需在“语音”页显式开启（或 `AIASSISTANT_VOICE_RECORD_COMMAND_AUDIO=true`） | 已实现加密保存与到期擦除；暂无内置回听界面，需从加密媒体库导出后播放 |

## 集成与交付

| 限制 | 影响 | 现状 |
|---|---|---|
| 其他 MCP 客户端未联调 | Claude Desktop 等客户端可能仍有会话/SSE 差异 | `S8-R2`，已通过 MCP Inspector 2.16.0 全链路联调并提供冒烟脚本 |
| PDF 可检索性依赖字体 | 自动扫描系统 TrueType CJK 字体；极简 Linux 无字体时回退矢量轮廓、文本不可检索 | 安装 CJK 字体或设置 `AIASSISTANT_PDF_FONT_PATH`；`--doctor` 显示当前选择 |
| 多日浸泡未执行 | 休眠/唤醒、跨午夜长跑风险 | `S5-R2`，S2 已完成 24h 加速浸泡 |
| Windows 签名/MSI 未在本机构建 | 需证书与 Windows SDK | 脚本已就绪，发布工作流需配置 Secrets |
| Linux `.deb` 仅在装有 `dpkg-deb` 的机器产出 | 其他环境只有 tar.gz | 打包脚本按工具存在性降级 |
| 安装包卸载不删除数据 | 需手动清理 | 见 `upgrade-and-uninstall.md` |

## 安全边界

- 加密保护的是「磁盘上的数据」；应用运行期间内存中的明文由操作系统隔离与用户账户权限保护。
- 密钥库条目被删除后，历史数据库无法解密（故障注入测试覆盖该路径，会给出可诊断错误而不是静默损坏）。
- MCP 只读端点不返回媒体字节，但会返回事件摘要；启用前请确认本机账户可信。
