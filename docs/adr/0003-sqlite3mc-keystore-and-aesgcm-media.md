# ADR-0003：采用 SQLite3MC 加密数据库、系统密钥库与媒体 AES-GCM 加密

- **状态**：已采纳 (Accepted)
- **最后更新**：2026-11-20
- **适用版本**：0.1.x
- **维护触发条件**：决策被取代或修订时新增 ADR，本文件结论不改
- **阶段**：S0 / S1

## 背景

`SQLitePCLRaw.bundle_e_sqlcipher` 已进入弃用/停更路径；此外若仅加密 SQLite 数据库而将截图与录音明文存放在磁盘目录中，离线拷贝仍会导致严重隐私泄露。

## 决策

1. 数据库驱动采用 `Microsoft.Data.Sqlite.Core` + `SQLite3MC.PCLRaw.bundle`（SQLite3 Multiple Ciphers，MIT 许可），连接打开后立即执行密钥配置并校验库头可读性，启用 WAL 与 `busy_timeout`。
2. 主密钥由各平台系统密钥库保护：Windows 使用 DPAPI（`ProtectedData`），macOS 使用 Keychain，Linux 使用 `libsecret`，支持主密钥轮换（DEK/KEK 双层包装，轮换 KEK 时无需重写全量媒体文件）。
3. 外部原始媒体（截图、缩略图、音频块）统一经 HKDF 派生子密钥后以 **AES-256-GCM**（随机 12 字节 Nonce + 16 字节 Tag + 魔数版本头）落盘，通过 `content_refs` 引用计数管理生命周期，UI 按需在内存解密读取，不留明文临时文件。

## 后果

- 错误密钥、文件篡改或权限丢失会立即抛出可诊断的领域异常（`StorageAuthenticationException` / `StorageIntegrityException`），绝不静默降级为明文库。
