# native/ — 原生工程

本目录只放非托管/原生工程，不放 .NET 代码。

| 路径 | 内容 |
|---|---|
| `AiAssistant.MacHelper/` | Swift 包：macOS 原生能力（屏幕捕获、窗口与文件活动、Vision OCR、音频播放、Keychain、TCC 权限） |

## 约束

- .NET 侧通过版本化 Unix Domain Socket IPC 调用（见 [ADR-0005](../docs/adr/0005-macos-swift-helper-ipc.md)），协议变更必须升版本号并保持向后兼容。
- 包名 `AiAssistant.MacHelper` 属于品牌派生命名，按 R3 例外保留 `Mac` 写法。
- SwiftPM 中间产物统一落在 `artifacts/obj/native/mac-helper/`，源码树不产生 `.build/`；安装包只带 `aiassistant-mac-helper` 可执行文件。

## 构建

```bash
bash build/native/mac-helper.sh build
bash build/native/mac-helper.sh smoke
bash build/native/mac-helper.sh test
```

`build/package/package-macos.sh` 会在 `Contents/MacOS/` 中一并打包该二进制；缺失时只告警，正式发布需先构建。
