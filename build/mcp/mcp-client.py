#!/usr/bin/env python3
"""MCP conformance client for the AiAssistant read-only local endpoint.

Performs the Streamable-HTTP handshake, tool discovery and read-only tool calls, then verifies
JSON-RPC error codes, SSE fallback and (optionally) unauthenticated rejection.

Usage:
    python build/mcp/mcp-client.py --token <token from the diagnostics panel>
    python build/mcp/mcp-client.py --endpoint http://127.0.0.1:47831/mcp --accept sse --check-unauthorized
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.error
import urllib.request
from datetime import date, timedelta

SUPPORTED_VERSIONS = {"2024-11-05", "2025-03-26", "2025-06-18"}
WRITE_VERBS = ("write", "delete", "remove", "create", "capture", "pause", "resume", "set", "export", "rotate")


class Client:
    def __init__(self, endpoint: str, token: str, accept: str, timeout: float):
        self.endpoint = endpoint
        self.token = token
        self.accept = accept
        self.timeout = timeout

    def post(self, payload, token: str | None = None, accept: str | None = None):
        data = payload if isinstance(payload, bytes) else json.dumps(payload).encode("utf-8")
        request = urllib.request.Request(self.endpoint, data=data, method="POST")
        request.add_header("Content-Type", "application/json")
        effective_accept = accept or self.accept
        if effective_accept == "sse":
            request.add_header("Accept", "text/event-stream")
        elif effective_accept == "json":
            request.add_header("Accept", "application/json")
        else:
            request.add_header("Accept", "application/json, text/event-stream")
        bearer = token if token is not None else self.token
        if bearer:
            request.add_header("Authorization", f"Bearer {bearer}")

        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                return response.status, dict(response.headers), response.read().decode("utf-8")
        except urllib.error.HTTPError as error:
            return error.code, dict(error.headers), error.read().decode("utf-8")

    @staticmethod
    def decode(status, headers, body):
        content_type = headers.get("Content-Type", "")
        if content_type.startswith("text/event-stream"):
            for line in body.splitlines():
                if line.startswith("data: "):
                    return json.loads(line[len("data: "):])
            raise ValueError("SSE response contained no data event")
        return json.loads(body) if body.strip() else None


class Checker:
    def __init__(self):
        self.failures = 0
        self.total = 0

    def check(self, name: str, condition: bool, detail: str = ""):
        self.total += 1
        if condition:
            print(f"PASS  {name}")
        else:
            self.failures += 1
            suffix = f" — {detail}" if detail else ""
            print(f"FAIL  {name}{suffix}", file=sys.stderr)

    def summary(self) -> int:
        print(f"\n{self.total - self.failures}/{self.total} checks passed")
        return 0 if self.failures == 0 else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--endpoint", default="http://127.0.0.1:47831/mcp")
    parser.add_argument("--token", default=os.environ.get("AIASSISTANT_MCP_TOKEN", ""))
    parser.add_argument("--accept", choices=["json", "sse", "both"], default="both")
    parser.add_argument("--timeout", type=float, default=10.0)
    parser.add_argument("--check-unauthorized", action="store_true")
    args = parser.parse_args()

    if not args.token:
        print("Missing token: pass --token or set AIASSISTANT_MCP_TOKEN", file=sys.stderr)
        return 2

    client = Client(args.endpoint, args.token, args.accept, args.timeout)
    checker = Checker()

    status, headers, body = client.post(
        {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": {
                "protocolVersion": "2025-03-26",
                "capabilities": {},
                "clientInfo": {"name": "aiassistant-conformance", "version": "1.0"},
            },
        }
    )
    init = client.decode(status, headers, body) if status == 200 else None
    result = (init or {}).get("result", {})
    checker.check("initialize returns 200", status == 200, f"status={status} body={body[:200]}")
    checker.check(
        "protocol version negotiated",
        result.get("protocolVersion") in SUPPORTED_VERSIONS,
        str(result.get("protocolVersion")),
    )
    checker.check("server info present", result.get("serverInfo", {}).get("name") == "aiassistant-local")
    checker.check("session header present", bool(headers.get("Mcp-Session-Id")))

    status, headers, body = client.post({"jsonrpc": "2.0", "method": "notifications/initialized"})
    checker.check("notification accepted with 202", status == 202 and body.strip() == "", f"status={status}")

    status, headers, body = client.post({"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
    listing = client.decode(status, headers, body)
    tools = listing.get("result", {}).get("tools", []) if listing else []
    names = {tool.get("name") for tool in tools}
    checker.check("tools/list returns 200", status == 200)
    checker.check(
        "only read-only tools exposed",
        names == {"search_events", "activity_stats", "list_reports", "get_report"},
        str(sorted(names)),
    )
    checker.check(
        "no write-like tool names",
        not any(verb in (name or "").lower() for name in names for verb in WRITE_VERBS),
    )
    checker.check(
        "every tool has an object schema",
        all(tool.get("inputSchema", {}).get("type") == "object" for tool in tools),
    )

    today = date.today()
    status, headers, body = client.post(
        {
            "jsonrpc": "2.0",
            "id": 3,
            "method": "tools/call",
            "params": {
                "name": "activity_stats",
                "arguments": {
                    "fromDate": (today - timedelta(days=6)).isoformat(),
                    "toDate": today.isoformat(),
                },
            },
        }
    )
    call = client.decode(status, headers, body)
    content = (call or {}).get("result", {}).get("content", [])
    checker.check("tools/call returns 200", status == 200)
    checker.check("tool result uses text content", bool(content) and content[0].get("type") == "text")
    payload = json.loads(content[0]["text"]) if content else {}
    checker.check("activity_stats returns daily buckets", isinstance(payload.get("days"), list), str(payload)[:200])

    status, headers, body = client.post({"jsonrpc": "2.0", "id": 4, "method": "ping"})
    ping = client.decode(status, headers, body)
    checker.check("ping returns empty object", status == 200 and ping.get("result") == {})

    status, headers, body = client.post(
        {
            "jsonrpc": "2.0",
            "id": 5,
            "method": "tools/call",
            "params": {"name": "delete_events", "arguments": {}},
        }
    )
    rejection = client.decode(status, headers, body)
    error = (rejection or {}).get("error", {})
    checker.check(
        "write tool rejected with -32602",
        error.get("code") == -32602 and "read-only" in error.get("message", "").lower(),
        str(error),
    )

    status, headers, body = client.post({"jsonrpc": "2.0", "id": 6, "method": "resources/list"})
    unknown = client.decode(status, headers, body)
    checker.check("unknown method returns -32601", unknown.get("error", {}).get("code") == -32601)

    status, headers, body = client.post(b"{")
    parse_error = client.decode(status, headers, body)
    checker.check("malformed JSON returns -32700", parse_error.get("error", {}).get("code") == -32700)

    if args.accept == "sse":
        status, headers, body = client.post({"jsonrpc": "2.0", "id": 7, "method": "ping"}, accept="sse")
        checker.check(
            "SSE response uses event-stream content type",
            headers.get("Content-Type", "").startswith("text/event-stream"),
            headers.get("Content-Type", ""),
        )
        checker.check("SSE body carries a data event", "event: message" in body and "data: " in body)

    if args.check_unauthorized:
        status, headers, body = client.post({"jsonrpc": "2.0", "id": 8, "method": "ping"}, token="")
        checker.check("missing token rejected with 401", status == 401, f"status={status}")

    return checker.summary()


if __name__ == "__main__":
    raise SystemExit(main())
