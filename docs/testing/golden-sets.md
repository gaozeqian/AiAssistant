# 识别黄金集（ASR / OCR）

> 对应 `IMPLEMENTATION.md` S4 第 6 项与 `PROGRESS.md` 遗留项 `S4-R1` / `S4-R2` / `S4-R3` / `S7-R1`。
> 模型资产与真实语料都不入库；`artifacts/` 已在 `.gitignore` 中。

## 1. 目录约定

```text
artifacts/golden/
├── asr/
│   ├── quiet-01.wav        # 安静环境，普通话
│   ├── quiet-01.txt        # 参考答案（UTF-8，一行）
│   ├── noisy-01.wav
│   ├── noisy-01.txt
│   └── ...
└── ocr/
    ├── settings-zh-01.png
    ├── settings-zh-01.txt
    └── ...
```

- 媒体与参考答案同名，仅扩展名不同；缺 `.txt` 的样本会被跳过并在报告 `警告` 段落列出。
- ASR 支持任意采样率与单/双声道（自动转 16 kHz 单声道）；推荐直接录 16 kHz 单声道 PCM16。
- OCR 的托管解码器只接受 PNG（JPEG 会报 `unsupported image format`）。

## 2. 语料覆盖建议

| 维度 | ASR | OCR |
|---|---|---|
| 环境 | 安静 / 键盘声 / 外放音乐 | — |
| 设备 | 内置麦克风 / 耳机麦 / USB 麦 | — |
| 口音与语速 | 正常 / 偏快 / 中英混说 | — |
| 近音词 | 「时光 / 石光」「会议 / 回忆」 | — |
| 多人 | 他人插话、旁白 | — |
| 界面 | — | 浅色/深色、100%/150% 缩放、中文为主 + 少量英文 |

单集建议 20–50 条，且**不要**使用真实个人录音或含敏感信息的截图；用合成文本朗读或明确授权的素材。

## 3. 运行

```bash
# 默认读取 artifacts/golden/asr，报告写入 artifacts/benchmarks/asr-<时间>.md
AiAssistant.Host --benchmark-asr

# 指定语料与输出
AiAssistant.Host --benchmark-asr --samples /data/golden/asr --output artifacts/benchmarks/asr.md

# OCR（macOS 走 Vision；Linux 走 RapidOCR）
AIASSISTANT_RAPIDOCR_MODEL_DIR=/models/rapidocr \
  AiAssistant.Host --benchmark-ocr --samples /data/golden/ocr
```

模型位置：

- ASR：`AIASSISTANT_SHERPA_MODEL_DIR`（默认 sherpa）；`AIASSISTANT_ASR_ENGINE=whisper` + `AIASSISTANT_WHISPER_MODEL_PATH` 可切 Whisper 做基线对比。
- macOS 无需模型资产（使用系统 Vision，经 Swift Helper 调用），但需要 Helper 可用（`AIASSISTANT_MAC_HELPER_PATH` 或打包产物）。
- Linux OCR：`AIASSISTANT_RAPIDOCR_MODEL_DIR`（含 `ch_PP-OCRv4_det_infer.onnx`、`ch_PP-OCRv4_rec_infer.onnx`、`ppocr_keys_v1.txt`）。

## 4. 报告口径

- `CER = 编辑距离 / 参考字数`，比较前会去掉空白、标点、符号并统一小写；插入过多时 CER 可超过 100%。
- `完全匹配` 只统计 CER = 0 的样本。
- 模型缺失时整批标记 `engine unavailable`，**不**计为 100% 错误；命令退出码 `1`。
- 退出码：`0` 至少一条成功；`1` 无成功样本（缺模型 / 缺语料 / 全部失败）；`2` 启动失败。

## 5. 已有基线

- `docs/testing/baselines/2026-11-21-macos-arm64.md`：合成语料 ASR CER 12.45%、OCR CER 0.00%（macOS Vision）。

## 6. 待补齐

- `S7-R1`：KWS 唤醒率 / 误唤醒率需要流式回放语料（正样本 + 负样本连续音频），当前尚未提供 `--benchmark-kws`。
- 真实脱敏语料扩充后，把 `--benchmark-retrieval` 的查询集也替换为真实分布（`S8-R1`）。
