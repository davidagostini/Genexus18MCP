#!/usr/bin/env python3
"""Live verification of the AutoTypeInjector Table-shadow fix.

Before the fix: genexus_edit on a Transaction with `type` omitted made the
gateway auto-inject type="Table" (the physical table shadow won the name→type
map), the worker resolved the table object, and the patch failed with
PatchReadFailed ("The object does not expose the requested part").

After the fix: the injector refuses to inject "Table", the call stays
type-less, the worker's global resolution picks the Transaction, and the edit
proceeds normally (mode=full dryRun → status=ok / code=WriteDryRun).

Assertions: (1) response._meta.autoInjected is absent; (2) status == ok.
Read-only + dryRun; nothing persists."""
import json
import sys
import time
import urllib.request

BASE = "http://127.0.0.1:5000/mcp"
KB = sys.argv[1] if len(sys.argv) > 1 else "C:/KBs/KBTeste"
ALIAS = "live"
TARGET = sys.argv[2] if len(sys.argv) > 2 else "TrnGroupProbeSub"


def rpc(sid, method, params, timeout=180, is_notification=False):
    payload = {"jsonrpc": "2.0", "method": method, "params": params}
    if not is_notification:
        payload["id"] = 1
    body = json.dumps(payload).encode()
    req = urllib.request.Request(BASE, data=body, method="POST", headers={
        "Accept": "application/json, text/event-stream",
        "Content-Type": "application/json",
    })
    if sid:
        req.add_header("MCP-Session-Id", sid)
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        raw = resp.read().decode("utf-8", errors="replace")
    try:
        outer = json.loads(raw)
        txt = outer["result"]["content"][0]["text"]
        return json.loads(txt)
    except Exception:
        return {"__raw__": raw[:200]}


body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                   "params": {"protocolVersion": "2025-03-26", "capabilities": {},
                              "clientInfo": {"name": "probe-autoinject-fix", "version": "1"}}}).encode()
req = urllib.request.Request(BASE, data=body, method="POST", headers={
    "Accept": "application/json, text/event-stream",
    "Content-Type": "application/json"})
with urllib.request.urlopen(req, timeout=30) as resp:
    sid = resp.headers.get("MCP-Session-Id")
    resp.read()
rpc(sid, "notifications/initialized", {}, is_notification=True)
time.sleep(1)

env = rpc(sid, "tools/call", {"name": "genexus_kb", "arguments": {"action": "open", "path": KB, "alias": ALIAS}}, timeout=240)
print("open:", env.get("status"))

# Read the target's source so mode=full has real content (+ marker line).
rd = rpc(sid, "tools/call", {"name": "genexus_read",
                             "arguments": {"kb": ALIAS, "name": TARGET, "part": "Source", "limit": 0}}, timeout=120)
src = (rd or {}).get("source") or ""
content = (src.rstrip() + "\n// gxbench-dryrun") if src.strip() else None

# THE test: genexus_edit with NO type — the pre-fix gateway injected "Table".
ed = rpc(sid, "tools/call", {"name": "genexus_edit", "arguments": {
    "kb": ALIAS, "name": TARGET, "part": "Source", "mode": "full",
    "content": content, "dryRun": True}}, timeout=180)

meta = (ed or {}).get("_meta") or {}
auto = meta.get("autoInjected")
auto_type = meta.get("autoInjectedType")
code = (ed or {}).get("code")
status = (ed or {}).get("status")
print(f"edit (no type): status={status} code={code}")
print(f"  _meta.autoInjected={auto} autoInjectedType={auto_type}")
if auto is None and status == "ok":
    print("RESULT: PASS — no auto-injection of type=Table; edit resolved to the Transaction")
elif auto is not None and str(auto_type).lower() == "table":
    print(f"RESULT: FAIL — auto-injected type={auto_type}; pre-fix shadow behavior still present")
else:
    print(f"RESULT: CHECK — unexpected envelope: {json.dumps(ed)[:400]}")
print("PROBE DONE")
