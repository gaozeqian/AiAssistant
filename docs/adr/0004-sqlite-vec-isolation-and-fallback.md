# ADR-0004：sqlite-vec 接口隔离、版本元数据与加密兼容回退策略

- **状态**：已采纳 (Accepted)
- **日期**：2026-11-20
- **阶段**：S0 / S1

## 背景

`sqlite-vec` 处于 pre-v1 阶段，且不同 RID（`win-x64`、`osx-arm64`、`linux-x64`）下动态加载原生扩展与 `SQLite3MC` 加密引擎符号表的兼容性存在不确定风险。

## 决策

1. 业务层只依赖 `IVectorIndex` 端口，不感知底层 SQL 表或扩展语法。
2. 数据库维护 `vector_index_meta(index_name, model_id, model_version, dimensions, backend, updated_at_utc)`；当模型或维度变化时显式触发重建索引。
3. 提供双后端自动探测与同接口回退：
   - 首选尝试在 `SQLite3MC` 连接上加载 `sqlite-vec`（`vec0`）；
   - 若当前 RID 原生库缺失或扩展符号冲突，自动回退至同处于加密 SQLite 库内的 `EncryptedSqliteVectorIndex`（将 `float32[]` 存入加密表 BLOB，结合 `TensorPrimitives.CosineSimilarity` SIMD 加速检索，个人年均十万级事件检索延迟 `<15ms`），**绝不为了使用向量扩展而放弃数据库加密**。

## 后果

- S1 门禁在不牺牲加密强度的前提下确保三平台 100% 可用与可备份恢复。
