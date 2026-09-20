#!/usr/bin/env python3
"""Confirms the Table-shadow hypothesis: the KB index exposes BOTH a Table and
a Transaction entry under the same name (TrnGroupProbeSub). This is what lets
AutoTypeInjector prime name→"Table" from the top-5 RecentlyChanged window.
Read-only; nothing persists."""
import json
import sys
import time
import urllib.request

BASE = "http://127.0.0.1:5000/mcp"
KB = sys.argv[1] if len(sys.argv) > 1 else "C:/KBs/KBTeste"
ALIAS = "live"


def rpc(sid, method, params, timeout=120, is_notification=False):
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
                              "clientInfo": {"name": "probe-table-shadow", "version": "1"}}}).encode()
req = urllib.request.Request(BASE, data=body, method="POST", headers={
    "Accept": "application/json, text/event-stream",
    "Content-Type": "application/json"})
with urllib.request.urlopen(req, timeout=30) as resp:
    sid = resp.headers.get("MCP-Session-Id")
    resp.read()
rpc(sid, "notifications/initialized", {}, is_notification=True)
time.sleep(1)

env = rpc(sid, "tools/call", {"name": "genexus_kb", "arguments": {"action": "open", "path": KB, "alias": ALIAS}}, timeout=240)
print("open:", env.get("status"), env.get("opened"))

for tf in ("Transaction", "Table"):
    env = rpc(sid, "tools/call", {"name": "genexus_list_objects",
                                  "arguments": {"kb": ALIAS, "typeFilter": tf, "limit": 30}}, timeout=120)
    names = [r.get("name") for r in (env or {}).get("results", []) if r.get("name")]
    hits = [n for n in names if "Trn" in n or "Live" in n]
    print(f"typeFilter={tf}: {len(names)} objects; probe hits: {hits}")
print("PROBE DONE")
