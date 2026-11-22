# 随包模型资产与许可证

安装包会携带 `artifacts/models/bundle`（KWS / Paraformer ASR / MeloTTS TTS / Silero VAD）。对话与 Embedding 模型不随包分发。

> 核查日期：2026-11-22。核查依据：ModelScope API 的 License 字段、模型归档内的 LICENSE/README、ONNX 内嵌 metadata、上游仓库与 issue 回复。逐条证据见下文。

| 资产 | 包内文件 | 来源 | 体积 | 许可证结论 |
|---|---|---|---|---|
| KWS 唤醒词 | `sherpa/kws/*` | [sherpa-onnx kws-models / sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01](https://github.com/k2-fsa/sherpa-onnx/releases/tag/kws-models) | 4.9MB | **Apache-2.0**（模型 README 明示），可随包 |
| Paraformer ASR | `sherpa/model.int8.onnx`, `sherpa/tokens.txt` | [sherpa-onnx asr-models / sherpa-onnx-paraformer-zh-small-2024-03-09](https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models)，源自 ModelScope `crazyant/...-onnx`，再上游 `damo/speech_paraformer_asr_nat-zh-cn-16k-common-vocab8358-tensorflow1` | 78MB | **Apache-2.0，可随包**；须附许可证全文、归属链与修改说明（见证据 1） |
| MeloTTS TTS | `sherpa/tts/model.onnx`、`tokens.txt`、`lexicon.txt`、`*.fst`、`dict/` | [sherpa-onnx tts-models / vits-melo-tts-zh_en](https://github.com/k2-fsa/sherpa-onnx/releases/tag/tts-models)，上游 [myshell-ai/MeloTTS](https://github.com/myshell-ai/MeloTTS) | 权重 163MB + 词典/FST 20MB（包内 183MB） | **MIT，可随包**；保留版权与许可声明即可（见证据 2） |
| Silero VAD | `sherpa/silero_vad.onnx` | [sherpa-onnx asr-models / silero_vad_v5.onnx](https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models)，上游 [snakers4/silero-vad](https://github.com/snakers4/silero-vad) | 2.3MB | **MIT**（Silero VAD 仓库），可随包 |

> 体积说明：TTS 从 39MB 的 AISHELL-3 权重换成 MeloTTS 后，随包体积由 126MB 增至 **268MB**（26 个文件，+142MB），实测于 2026-11-22。打包前以 `bash build/models/stage-bundle.sh` 的输出为准。

## 证据 1：Paraformer ASR 的许可链

1. **上游 DAMO 模型明确为 Apache-2.0**。ModelScope API `GET /api/v1/models/damo/speech_paraformer_asr_nat-zh-cn-16k-common-vocab8358-tensorflow1` 返回 `"License":"Apache License 2.0"`，归属 `iic`（阿里巴巴达摩院）。
2. **中间导出件没有声明许可**。`GET /api/v1/models/crazyant/speech_paraformer_asr_nat-zh-cn-16k-common-vocab8358-onnx` 返回 `"License":""`、`"LicenseLink":""`、`"LicenseName":""`；该模型 README 只写"模型为 damo ... 的 onnx 量化导出版本"。
3. **本地权重同样没有许可信息**。`model.int8.onnx` 内嵌 metadata 只有 `model_type=paraformer`、`model_author=crazyant`、`url=https://www.modelscope.cn/models/crazyant/...`、`vocab_size=8359`，无 `license` 字段；归档内只有一份指向 crazyant 的 README。
4. **sherpa-onnx 不另行授权**。[issue #3930](https://github.com/k2-fsa/sherpa-onnx/issues/3930)（2026-09-07）维护者回复：模型并非其训练，"please refer to the licence of the original model. We always use the original licence."
5. **结论**：Apache-2.0 允许商业使用与再分发（含闭源产品内分发），但依第 4 条必须：(a) 提供许可证副本；(b) 保留版权与归属；(c) 对修改过的部分作显著说明；(d) 上游含 NOTICE 则一并保留。

## 证据 2：TTS 已替换为 MeloTTS（MIT）

**为什么换掉 AISHELL-3 VITS**：`vits-zh-aishell3` 的归档只有 `model.onnx`、`tokens.txt`、`lexicon.txt`，无 LICENSE/README；ONNX metadata 只有 `model_type=vits`、`comment=aishell3`，无 `license`；sherpa-onnx 文档与 release 页均无许可声明。训练集 AISHELL-3 在 [OpenSLR #93](https://www.openslr.org/93/) 标 `Apache License v.2.0`，但发布方口径的说明（PaddleSpeech `dataset/aishell3/README.md` 转载原文）写 "This database is free for academic research, not in the commerce, if without permission."。条款冲突且权重无授权，闭源商业随包无法成立，因此移除。

**新资产 MeloTTS 的依据**：

1. 上游 [myshell-ai/MeloTTS](https://github.com/myshell-ai/MeloTTS) 的 `LICENSE` 为 MIT（`Copyright (c) 2024 MyShell.ai`），允许商业使用、修改、再分发与再许可。
2. sherpa-onnx 的 `vits-melo-tts-zh_en` 由该模型转换而来（文档写明 "converted from myshell-ai/MeloTTS-Chinese"），模型内嵌 metadata 含 `license=MIT`。
3. 转换件必须携带 `dict/` 与 `date.fst` / `number.fst` / `phone.fst` / `new_heteronym.fst`；缺这些文件时数字、电话与多音字读法不正确。运行时已按存在性传参，缺失不会导致启动失败。
4. 代价：单说话人、44.1kHz、中英混读；随包体积增加约 120MB；音色与旧 AISHELL-3 权重不同，需重跑 TTS 与语音页验收。

## 处置结论

| 资产 | 处置 | 必须动作 |
|---|---|---|
| Paraformer ASR | 保留随包 | `THIRD-PARTY-NOTICES` 收录 Apache-2.0 全文与归属链：© Alibaba DAMO Academy（damo 模型）→ crazyant（ONNX int8 量化与格式转换）→ sherpa-onnx（重新打包）；声明本产品未再修改权重 |
| MeloTTS TTS | 保留随包（已替换 AISHELL-3） | NOTICE 收录 MIT 全文与 `Copyright (c) 2024 MyShell.ai`；随包保留 `sherpa/tts/LICENSE`（若归档提供） |
| KWS / Silero VAD | 保留随包 | NOTICE 收录 Apache-2.0（KWS）与 MIT（Silero）全文及版权行 |
| AISHELL-3 VITS | 已移除 | 归档 `vits-zh-aishell3.tar.bz2` 与解压目录移入 `artifacts/models/_retired/`，打包脚本不得再引用 |

## 发布前动作

1. `THIRD-PARTY-NOTICES.md` 已生成（2026-11-22，含 Apache-2.0 全文、Paraformer 归属链、MeloTTS / Silero VAD / ONNX Runtime 的 MIT 全文），三个平台的打包脚本会强制复制；缺失时打包直接失败。
2. 打包脚本加校验：产物中出现 `vits-zh-aishell3`、`aishell3` 或缺失 `dict/`、`*.fst` 时直接失败（`stage-bundle.sh` 已实现前者与后者）。
3. 重新生成 bundle 并实测体积：`bash build/models/fetch-bundle.sh`（或手工放置资产后 `bash build/models/stage-bundle.sh`）。
4. 若后续启用 `--with-whisper`（MIT）或 `--with-rapidocr`（PaddleOCR Apache-2.0），同步更新本表与 NOTICE。
5. 每次升级模型版本时重新核对 License 字段与归档内容，并记录核查日期与证据链接；同步更新 `THIRD-PARTY-NOTICES.md` 的组件表与许可证全文。

> 本表只记录工程事实与可核验证据，不构成法律意见。闭源商业发布前建议由法务复核 NOTICE 的呈现方式。
