#!/usr/bin/env python3
"""One-shot correctness probe for the 3 new bench ops (edit_dryrun,
analyze, lifecycle_status) against the live gateway.

Prints each envelope's status AND elapsed ms so the bench harness can be
trusted to measure the SUCCESS path, not a validation-error or gateway-timeout
path. Read-only + dryRun only; nothing persists.

Lesson driving the shapes: on a ~38k-object KB, `genexus_analyze mode=impact`
(caller-walk) and an edit patch on a big WebForm source both exceed the
gateway's ~60s synchronous cap for tracked ops without a client progress
token, so they return 'Gateway timeout' envelopes. The measurable shapes are
analyze mode=summary and an edit patch on a small Transaction source.
"""
import json
import re
import sys
import time
import urllib.request

BASE = "http://127.0.0.1:5000/mcp"
KB = sys.argv[1] if len(sys.argv) > 1 else "C:/KBs/KBTeste"
ALIAS = "live"


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
    t0 = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as e:
        return (time.perf_counter() - t0) * 1000.0, {"__http_error__": e.code}
    elapsed = (time.perf_counter() - t0) * 1000.0
    try:
        outer = json.loads(raw)
        txt = outer["result"]["content"][0]["text"]
        return elapsed, json.loads(txt)
    except Exception:
        return elapsed, {"__raw__": raw[:300]}


def find_long_string(node, min_len=40):
    if isinstance(node, str):
        return node if len(node) >= min_len else None
    if isinstance(node, dict):
        for v in node.values():
            r = find_long_string(v, min_len)
            if r:
                return r
    elif isinstance(node, list):
        for v in node:
            r = find_long_string(v, min_len)
            if r:
                return r
    return None


def first_identifier(text):
    m = re.search(r"[A-Za-z_][A-Za-z0-9_]{2,}", text or "")
    return m.group(0) if m else None


def status_of(env):
    if env is None:
        return "NULL"
    if "__http_error__" in env:
        return "HTTP %s" % env["__http_error__"]
    if "__raw__" in env:
        return "RAW:" + env["__raw__"]
    if env.get("isError") or env.get("error"):
        return "ERROR %s" % (env.get("error") or env.get("message"))
    # A dry-run success carries code='WriteDryRun' WITH status='ok'; real
    # failures (PatchReadFailed, NoMatch) carry a code but no ok status.
    st = str(env.get("status") or "").strip().lower()
    if st in ("ok", "success"):
        return "OK"
    if env.get("code"):
        return "ERROR %s" % env.get("code")
    return "OK"


def read_content_text(env):
    """Extract source text from a genexus_read envelope (content/lines/source/
    text priority). Needed for short Transaction sources the recursive dig
    (>=40-char strings) misses."""
    if not isinstance(env, dict):
        return None
    for key in ("content", "lines", "source", "text"):
        v = env.get(key)
        if isinstance(v, list):
            return "\n".join(str(x) for x in v[:40])
        if isinstance(v, str):
            return v
    return None


def envelope_is_ok(env):
    """True for a successful worker envelope. Success carries status='ok' — a
    dry-run write returns code='WriteDryRun' WITH status='ok', so a bare `code`
    field is NOT an error signal. Failures carry isError/error (or an error
    status with no ok status)."""
    if not isinstance(env, dict):
        return False
    if env.get("isError") or env.get("error"):
        return False
    st = str(env.get("status") or "").strip().lower()
    return st in ("ok", "success") or env.get("ok") is True


def show(label, elapsed, env):
    print(f"{label:20s} {status_of(env):30s} {elapsed:8.0f}ms  fields={sorted((env or {}).keys())[:8]}")


# handshake
body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                   "params": {"protocolVersion": "2025-03-26", "capabilities": {},
                              "clientInfo": {"name": "probe-bench-ops", "version": "1"}}}).encode()
req = urllib.request.Request(BASE, data=body, method="POST", headers={
    "Accept": "application/json, text/event-stream",
    "Content-Type": "application/json"})
with urllib.request.urlopen(req, timeout=30) as resp:
    sid = resp.headers.get("MCP-Session-Id")
    resp.read()
print(f"session: {sid}")
rpc(sid, "notifications/initialized", {}, is_notification=True)
time.sleep(1)

el, env = rpc(sid, "tools/call", {"name": "genexus_kb", "arguments": {"action": "open", "path": KB, "alias": ALIAS}}, timeout=240)
show("open KB", el, env)

ready = False
for _ in range(24):
    el, w = rpc(sid, "tools/call", {"name": "genexus_whoami", "arguments": {"kb": ALIAS}})
    st = ((w or {}).get("index") or {}).get("status", "?")
    print(f"  index={st}", flush=True)
    if st in ("Ready", "LiteReady", "Enriching"):
        ready = True
        break
    time.sleep(4)
print(f"index ready: {ready}")
time.sleep(2)

# Prefer a small Transaction — the edit patch target must be fast to measure.
el, env = rpc(sid, "tools/call", {"name": "genexus_list_objects",
                                  "arguments": {"kb": ALIAS, "typeFilter": "Transaction", "limit": 10}}, timeout=120)
names = [r.get("name") for r in (env or {}).get("results", []) if r.get("name")]
if not names:
    el, env = rpc(sid, "tools/call", {"name": "genexus_list_objects",
                                      "arguments": {"kb": ALIAS, "limit": 30}}, timeout=120)
    names = [r.get("name") for r in (env or {}).get("results", []) if r.get("name")]
if not names:
    names = ["TrnGroupProbeBase"]
name = names[0]
print(f"target: {name} (transactions: {names[:5]})")

el, env = rpc(sid, "tools/call", {"name": "genexus_lifecycle", "arguments": {"kb": ALIAS, "action": "status"}}, timeout=120)
show("lifecycle_status", el, env)

el, env = rpc(sid, "tools/call", {"name": "genexus_analyze", "arguments": {"kb": ALIAS, "name": name, "mode": "summary"}}, timeout=180)
show("analyze summary", el, env)

tok = None
edit_target = None
for cand in names:
    el, env = rpc(sid, "tools/call", {"name": "genexus_read",
                                      "arguments": {"kb": ALIAS, "name": cand, "part": "Source", "limit": 0}}, timeout=120)
    show(f"read {cand}", el, env)
    if not isinstance(env, dict) or env.get("isError") or env.get("error") \
            or "__http_error__" in env or "__raw__" in env:
        print(f"  read {cand} non-source envelope — skipping candidate")
        continue
    tok = first_identifier(read_content_text(env)) or first_identifier(find_long_string(env))
    if tok:
        edit_target = cand
        print(f"  token from {cand}: {tok}")
        break
    # Diagnostic: some Transactions (atomic-created probes) have an empty
    # Source part — show the envelope shape so extraction stays honest.
    print(f"  ENV KEYS for {cand}: {sorted((env or {}).keys())}")
    print(f"  ENV HEAD: {json.dumps(env)[:700]}")
if not tok:
    print("edit_dryrun: SKIPPED (no token extracted from any candidate)")
else:
    # Explicit type: without it the gateway auto-injects type="Table" for a
    # Transaction, which resolves to the table object (no Source part) and
    # fails the write read. And use mode=full with a marker line appended — a
    # patch find/replace needs byte-exact context and the read view can differ
    # from the patch view (observed NoMatch 'Context block not found'); mode=full
    # needs no matching and still exercises read -> write -> project -> diff.
    src_text = read_content_text(env) or ""
    content = (src_text.rstrip() + "\n// gxbench-dryrun") if src_text.strip() else None
    el, env = rpc(sid, "tools/call", {"name": "genexus_edit", "arguments": {
        "kb": ALIAS, "name": edit_target, "part": "Source", "mode": "full",
        "content": content,
        "dryRun": True, "type": "Transaction"}}, timeout=180)
    show("edit_dryrun", el, env)
    if isinstance(env, dict):
        print(f"  flags: applied={env.get('applied')} dryRun={env.get('dryRun')} "
              f"matched={env.get('expectedCount') or env.get('matched')} status={env.get('status')}")
        if not envelope_is_ok(env):
            print(f"  EDIT ENV: {json.dumps(env)[:1500]}")
print("PROBE DONE")
