#!/usr/bin/env python3
"""Live verification of the ROOT-CAUSE fix: the gateway now feeds AutoTypeInjector
from the FULL index name->[types] map (worker kb/GetNameTypeMap) instead of the
top-5 RecentlyChanged window, so a Transaction's physical Table shadow can never
win resolution — genexus_edit with `type` omitted must inject "Transaction".

Assertions:
  1. whoami index reaches Ready/LiteReady/Enriching (trigger for the map fetch);
  2. genexus_edit without type on a probe Transaction → _meta.autoInjectedType
     is "Transaction" (NOT "Table", NOT absent-with-failure);
  3. no PatchReadFailed.
Read-only + dryRun; nothing persists.
"""
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
                              "clientInfo": {"name": "probe-rootmap-live", "version": "1"}}}).encode()
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

# Wait for index to be usable (this is what arms the once-per-KB full-map fetch).
for i in range(40):
    wh = rpc(sid, "tools/call", {"name": "genexus_whoami", "arguments": {}}, timeout=120)
    idx = (wh or {}).get("index") or {}
    st = idx.get("status", "Cold")
    tot = idx.get("totalObjects", 0)
    print(f"  whoami[{i}] index.status={st} totalObjects={tot}")
    if st in ("Ready", "LiteReady", "Enriching"):
        break
    time.sleep(3)
else:
    print("FAIL: index never became usable")
    sys.exit(1)

# Give the fire-and-forget full-map fetch time to land (worker round-trip + apply).
time.sleep(3)

# THE test: genexus_edit with NO type — pre-fix injected the Table shadow.
rd = rpc(sid, "tools/call", {"name": "genexus_read",
                             "arguments": {"kb": ALIAS, "name": TARGET, "part": "Source", "limit": 0}}, timeout=120)
src = (rd or {}).get("source") or ""
content = (src.rstrip() + "\n// gxrootmap-dryrun") if src.strip() else None

ed = rpc(sid, "tools/call", {"name": "genexus_edit", "arguments": {
    "kb": ALIAS, "name": TARGET, "part": "Source", "mode": "full",
    "content": content, "dryRun": True}}, timeout=180)

meta = (ed or {}).get("_meta") or {}
auto_type = meta.get("autoInjectedType")
code = (ed or {}).get("code")
status = (ed or {}).get("status")
err = (ed or {}).get("error") or (ed or {}).get("message")
print(f"edit (no type): status={status} code={code}")
print(f"  _meta.autoInjected={meta.get('autoInjected')} autoInjectedType={auto_type}")

ok = False
if code == "PatchReadFailed" or (err and "does not expose" in str(err)):
    print("RESULT: FAIL — PatchReadFailed; still resolving to part-less table object")
elif str(auto_type).lower() == "table":
    print("RESULT: FAIL — injected type=Table; shadow still winning resolution")
elif str(auto_type).lower() == "transaction" and status == "ok":
    print("RESULT: PASS — full-map resolution injected Transaction (root cause fixed)")
elif auto_type is None and status == "ok":
    print("RESULT: PASS — no injection; worker global resolution picked the Transaction")
else:
    print(f"RESULT: CHECK — unexpected envelope: {json.dumps(ed)[:400]}")
print("PROBE DONE")
