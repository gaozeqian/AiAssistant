# ADR-0007：sherpa-onnx 统一语音技术栈与 AudioHub 单路麦克风分发

- **状态**：已采纳 (Accepted)
- **日期**：2026-11-20
- **阶段**：S0 / S4 / S7

## 背景

语音唤醒（“你好时光”）、日常活动录音 VAD、命令转写 ASR、说话人分离与本地 TTS 若分别使用不同框架或各自打开麦克风，会导致原生库体积膨胀与操作系统麦克风独占冲突。

## 决策

1. 语音相关能力优先统一至 `sherpa-onnx`（开放词表中文 KWS 免训练、VAD、流式/离线 ASR、说话人分离、TTS），保留 `Whisper.net` 作为可选高质量后台批处理适配器。
2. 通过 `IAudioHub` 单一入口采集麦克风音频，向多个订阅者广播只读帧；唤醒前仅在内存维护约 2 秒环形缓冲区，**绝不落盘、绝不外发**；关闭语音与录音开关后立即释放底层音频句柄与内存缓冲。
3. 原生 ONNX Runtime 由 `org.k2fsa.sherpa.onnx` 统一提供（当前 1.28.2，带版本化 SONAME）。其它推理能力（如 RapidOCR）只允许引用 `Microsoft.ML.OnnxRuntime.Managed` 托管绑定并复用该原生库；**禁止**再引入 `Microsoft.ML.OnnxRuntime`，否则会覆盖根目录 `libonnxruntime.so` 导致 sherpa 的 `VERS_1.28.2` 符号解析失败。

## 后果

- 待机资源极低，无麦克风设备争用，严格满足唤醒离线与唤醒前音频零落盘红线。
