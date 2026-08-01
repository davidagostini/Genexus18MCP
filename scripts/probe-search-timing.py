#!/usr/bin/env python3
"""Isolate search_source timing: real pattern search vs the bench's query-arg call.

The bench harness passes {'query': 'parm'} to genexus_search_source, but the
SearchRouter only maps pattern/callee — so the bench may be measuring an error
path, not a real search. This probe times both shapes plus the no-arg error.
"""
import json
import time
import urllib.request

BASE = "http://127.0.0.1:5000/mcp"


def rpc(sid, name, args, timeout=120):
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                       "params": {"name": name, "arguments": args}}).encode()
    req = urllib.request.Request(BASE, data=body, method="POST", headers={
        "Accept": "application/json, text/event-stream",
        "Content-Type": "application/json",
        "MCP-Session-Id": sid})
    t0 = time.perf_counter()
    with urllib.request.urlopen(req, timeout=timeout) as r:
        raw = r.read().decode("utf-8", errors="replace")
    return (time.perf_counter() - t0) * 1000.0, raw


def inner(raw):
    try:
        outer = json.loads(raw)
        return json.loads(outer["result"]["content"][0]["text"])
    except Exception as e:
        return {"parse_err": str(e)[:100]}


body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                   "params": {"protocolVersion": "2025-03-26", "capabilities": {},
                              "clientInfo": {"name": "probe", "version": "1.0"}}}).encode()
req = urllib.request.Request(BASE, data=body, method="POST", headers={
    "Accept": "application/json, text/event-stream",
    "Content-Type": "application/json"})
with urllib.request.urlopen(req, timeout=30) as r:
    sid = r.headers.get("MCP-Session-Id")
    r.read()
time.sleep(1)

el, _ = rpc(sid, "genexus_kb", {"action": "open", "path": "C:/KBs/KBTeste", "alias": "live"})
print(f"open KB: {el:.1f}ms")
time.sleep(3)

print("\n--- no-args (pure error path) ---")
for i in range(3):
    el, raw = rpc(sid, "genexus_search_source", {"kb": "live"})
    j = inner(raw)
    print(f"#{i}: {el:.1f}ms code={j.get('code') if isinstance(j, dict) else j}")

print("\n--- query-arg (bench's actual call) ---")
for i in range(4):
    el, raw = rpc(sid, "genexus_search_source", {"kb": "live", "query": "parm", "limit": 10})
    j = inner(raw)
    print(f"#{i}: {el:.1f}ms code={j.get('code') if isinstance(j, dict) else j}")

print("\n--- pattern-arg (real search) ---")
for i in range(4):
    el, raw = rpc(sid, "genexus_search_source", {"kb": "live", "pattern": "parm", "limit": 10})
    j = inner(raw)
    r = j.get("result", {}) if isinstance(j, dict) else {}
    print(f"#{i}: {el:.1f}ms code={j.get('code') if isinstance(j, dict) else j} "
          f"count={r.get('count')} scanned={r.get('scannedObjects')}")
