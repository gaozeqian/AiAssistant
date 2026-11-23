# MCP 客户端联调

本目录提供与第三方 MCP 客户端（MCP Inspector）联调的最小流程。服务端为只读、loopback、Bearer 令牌保护。

## 1. 启动服务

```bash
AIASSISTANT_DATA_DIRECTORY=artifacts/tmp/mcp-inspector/data \
AIASSISTANT_MCP_ENABLED=true \
AIASSISTANT_MCP_PORT=47839 \
dotnet run --project src/AiAssistant.Host/AiAssistant.Host.csproj -- --mcp-serve
```

另开终端取令牌（值只写不读，但 `--mcp-token` 是显式的本地操作）：

```bash
AIASSISTANT_DATA_DIRECTORY=artifacts/tmp/mcp-inspector/data dotnet run --project src/AiAssistant.Host/AiAssistant.Host.csproj -- --mcp-token
```

## 2. MCP Inspector CLI

固定版本，避免 `npx` 漂移：

```bash
npx --yes @modelcontextprotocol/inspector@2.16.0 --cli \
  --transport http \
  --server-url http://127.0.0.1:47839/mcp \
  --header "Authorization: Bearer $TOKEN" \
  --method initialize --format json

npx --yes @modelcontextprotocol/inspector@2.16.0 --cli \
  --transport http --server-url http://127.0.0.1:47839/mcp \
  --header "Authorization: Bearer $TOKEN" \
  --method tools/list --format json

npx --yes @modelcontextprotocol/inspector@2.16.0 --cli \
  --transport http --server-url http://127.0.0.1:47839/mcp \
  --header "Authorization: Bearer $TOKEN" \
  --method tools/call --tool-name activity_stats \
  --tool-args-json '{"fromDate":"2026-11-16","toDate":"2026-11-22"}' \
  --format json
```

预期：

- `initialize` 返回 `protocolVersion=2025-03-26`、`serverInfo.name=aiassistant-local`。
- `tools/list` 恰好返回 `search_events`、`activity_stats`、`list_reports`、`get_report`。
- `tools/call` 的 `result.content[0].type=text`，文本为结构化 JSON。
- 不带令牌：退出码 `3`（`auth_required`）。
- 调用 `delete_events`：退出码 `5`（`tool_not_found`，服务端没有写工具）。

## 3. 一键冒烟

```powershell
$env:AIASSISTANT_MCP_TOKEN = "<token>"
pwsh tools/mcp/inspector-smoke.ps1 -Endpoint http://127.0.0.1:47839/mcp
```

脚本校验握手、工具集合与 `activity_stats` 调用；需要 Node 20+ 与网络（首次下载 Inspector）。
