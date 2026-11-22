# 随包模型资产与许可证

安装包会携带 `artifacts/models/bundle`（KWS / Paraformer ASR / VITS TTS / Silero VAD，约 126MB）。
对话与 Embedding 模型不随包分发。

| 资产 | 包内文件 | 来源 | 体积 | 许可证状态 |
|---|---|---|---|---|
| KWS 唤醒词 | `sherpa/kws/*` | [sherpa-onnx kws-models / sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01](https://github.com/k2-fsa/sherpa-onnx/releases/tag/kws-models) | 4.9MB | **Apache-2.0**（模型 README 明示），可再分发 |
| Paraformer ASR | `sherpa/model.int8.onnx`, `sherpa/tokens.txt` | [sherpa-onnx asr-models / sherpa-onnx-paraformer-zh-small-2024-03-09](https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models)，源自 ModelScope `crazyant/speech_paraformer_asr_nat-zh-cn-16k-common-vocab8358-onnx` | 78MB | **待确认**：归档内 README 未附许可证，需在发布前核对上游 ModelScope 模型页 |
| VITS TTS | `sherpa/tts/*` | [sherpa-onnx tts-models / vits-zh-aishell3](https://github.com/k2-fsa/sherpa-onnx/releases/tag/tts-models) | 39MB（int8）+ 2MB lexicon | **待确认**：基于 AISHELL-3 训练；归档内无 LICENSE，需核对数据集/模型页条款 |
| Silero VAD | `sherpa/silero_vad.onnx` | [sherpa-onnx asr-models / silero_vad_v5.onnx](https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models)，上游 [snakers4/silero-vad](https://github.com/snakers4/silero-vad) | 2.3MB | **MIT**（Silero VAD 仓库） |

## 发布前动作

1. 确认 ASR 与 TTS 模型的上游许可证允许再分发；若不明确，改为构建期下载（不随安装包）或替换为明确 Apache-2.0/MIT 的模型。
2. 在安装包内加入 `THIRD-PARTY-NOTICES`（或安装器许可页），至少包含 KWS 与 Silero VAD 的版权与许可证全文，以及 ASR/TTS 的来源说明。
3. 若后续启用 `--with-whisper`（MIT）或 `--with-rapidocr`（PaddleOCR Apache-2.0），同步更新本表与 NOTICE。

> 本表只记录工程事实，不构成法律意见。
