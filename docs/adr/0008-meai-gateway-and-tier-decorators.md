# ADR-0008：基于 Microsoft.Extensions.AI 构建统一模型网关与三档策略装饰器

- **状态**：已采纳 (Accepted)
- **日期**：2026-11-20
- **阶段**：S0 / S5 / S6

## 背景

系统需支持本机离线（默认）、公司内网、公网 API 三档模型切换，同时强制执行脱敏、企业锁定策略（`AllowPublicCloud=false`）、不含正文的调用审计与能力探测。

## 决策

1. 业务层（摘要、检索问答）直接依赖 `Microsoft.Extensions.AI` 标准接口 `IChatClient` 与 `IEmbeddingGenerator<string, Embedding<float>>`。
2. `AiAssistant.Models` 以装饰器管线封装：企业策略门禁 → 正则/NER 统一脱敏 → 端点能力探测与档位路由 → 超时/回退 → 元数据审计。
3. 策略锁定在 `Host` 最外层强制生效，绕过 UI 直接调用应用层用例同样受阻断。

## 后果

- 切换 Ollama、vLLM 或云厂商仅改 `models.json` 配置，不改一行摘要或问答业务代码。
