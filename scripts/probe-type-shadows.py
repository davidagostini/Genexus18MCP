#!/usr/bin/env python3
"""Live probe: find name->multi-type collisions in the index (physical shadows).

For each candidate type filter, list object names via genexus_list_objects, then
report every name that appears under 2+ types. This empirically detects shadow
pairs beyond {Transaction, Table} — e.g. does a Domain or an Attribute also
generate a same-named sibling in the design model?
"""
import json
import sys
import time
import urllib.request

BASE = "http://127.0.0.1:5000/mcp"
KB = sys.argv[1] if len(sys.argv) > 1 else "C:/KBs/KBTeste"
ALIAS = "live"

# Candidates: every type that could be a source-bearing object OR a physical shadow.
TYPES = ["Transaction", "Table", "Domain", "Attribute", "SDT", "StructuredDataType",
         "WebPanel", "Procedure", "DataProvider", "BusinessComponent", "Image",
         "DataView", "ExternalObject", "Menu", "WorkPanel", "SDPanel"]


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
                              "clientInfo": {"name": "probe-type-shadows", "version": "1"}}}).encode()
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

# Wait for a usable index (list_objects requires it).
for i in range(40):
    wh = rpc(sid, "tools/call", {"name": "genexus_whoami", "arguments": {}}, timeout=120)
    st = ((wh or {}).get("index") or {}).get("status", "Cold")
    if st in ("Ready", "LiteReady", "Enriching"):
        break
    time.sleep(3)
print("index.status:", st)

# Collect names per type.
by_type = {}
for t in TYPES:
    env = rpc(sid, "tools/call", {"name": "genexus_list_objects",
                                  "arguments": {"kb": ALIAS, "typeFilter": t, "limit": 5000}}, timeout=180)
    if env is None or env.get("error"):
        names = []
    else:
        # list_objects envelopes: { count, total, results: [{name, type, ...}] }
        items = env.get("results") or env.get("objects") or env.get("items") or []
        names = [str(o.get("name") or o.get("Name")) for o in items]
    by_type[t] = set(names)
    print("  %-22s %4d objects" % (t, len(names)))
    time.sleep(0.4)

# Collisions: name in 2+ types.
name_types = {}
for t, names in by_type.items():
    for n in names:
        if not n:
            continue
        name_types.setdefault(n, []).append(t)

collisions = {n: ts for n, ts in name_types.items() if len(ts) >= 2}
print("\n==== %d names under 2+ types ====" % len(collisions))
for n in sorted(collisions):
    print("  %-28s -> %s" % (n, ", ".join(collisions[n])))

# Pairs summary (which type-pairs collide).
from collections import Counter
pair_count = Counter()
for n, ts in collisions.items():
    pair_count[tuple(sorted(set(ts)))] += 1
print("\n==== type-pair collision summary ====")
for pair, cnt in pair_count.most_common():
    print("  %-40s x%d" % (" + ".join(pair), cnt))

print("PROBE DONE")
