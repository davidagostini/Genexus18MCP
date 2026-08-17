import urllib.request
import json
import time
import os

url = "http://127.0.0.1:5000/mcp"

def post_rpc(data, session_id=None):
    headers = {
        "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream",
        "MCP-Protocol-Version": "2025-06-18"
    }
    if session_id:
        headers["MCP-Session-Id"] = session_id
    req = urllib.request.Request(url, data=json.dumps(data).encode("utf-8"), headers=headers, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            new_session = resp.headers.get("MCP-Session-Id") or session_id
            content_type = resp.headers.get("Content-Type", "")
            if "text/event-stream" in content_type:
                line = resp.readline().decode("utf-8")
                while line:
                    if line.startswith("data:"):
                        return json.loads(line[5:].strip()), new_session
                    line = resp.readline().decode("utf-8")
            else:
                raw = resp.read().decode("utf-8")
                return json.loads(raw), new_session
    except Exception as e:
        print(f"Error: {e}")
        return None, session_id

print("--- 1. Initialize ---")
init_req = {
    "jsonrpc": "2.0",
    "id": 1,
    "method": "initialize",
    "params": {
        "protocolVersion": "2025-06-18",
        "capabilities": {},
        "clientInfo": {"name": "TestClient", "version": "1.0"}
    }
}
resp, session_id = post_rpc(init_req)
print("Session ID:", session_id)

print("\n--- Worker Reload with bin/Debug ---")
reload_req = {
    "jsonrpc": "2.0",
    "id": 2,
    "method": "tools/call",
    "params": {
        "name": "genexus_worker_reload",
        "arguments": {"mode": "hard", "sourceDir": r"C:\Projetos\Genexus18MCP\src\GxMcp.Worker\bin\Debug"}
    }
}
resp, _ = post_rpc(reload_req, session_id)
print("Worker reload result:", resp)

# Open KB if needed
print("\n--- Open KB ---")
kb_req = {
    "jsonrpc": "2.0",
    "id": 3,
    "method": "tools/call",
    "params": {
        "name": "genexus_kb",
        "arguments": {"action": "open", "path": "C:\\KBs\\KBTeste"}
    }
}
resp, _ = post_rpc(kb_req, session_id)
print("KB open result:", resp)

# Clean up existing test obj if any
post_rpc({
    "jsonrpc": "2.0",
    "id": 4,
    "method": "tools/call",
    "params": {
        "name": "genexus_delete_object",
        "arguments": {"name": "ProcAutoVarTest", "confirm": True}
    }
}, session_id)

print("\n--- 2. Test Item A: autoDeclareVariables on Create/Edit ---")
create_req = {
    "jsonrpc": "2.0",
    "id": 5,
    "method": "tools/call",
    "params": {
        "name": "genexus_create",
        "arguments": {
            "name": "ProcAutoVarTest",
            "type": "Procedure",
            "source": "&NewCounter = 42\r\n&DescriptionTag = 'Test'",
            "autoDeclareVariables": True
        }
    }
}
resp, _ = post_rpc(create_req, session_id)
print("Create response:", resp)

read_var_req = {
    "jsonrpc": "2.0",
    "id": 6,
    "method": "tools/call",
    "params": {
        "name": "genexus_read",
        "arguments": {
            "name": "ProcAutoVarTest",
            "part": "Variables"
        }
    }
}
resp, _ = post_rpc(read_var_req, session_id)
text_res = resp.get("result", {}).get("content", [{}])[0].get("text", "")
print("Variables Part Read:\n", text_res)

print("\n--- 3. Test Item B: ExtractSubroutine ---")
refactor_req = {
    "jsonrpc": "2.0",
    "id": 7,
    "method": "tools/call",
    "params": {
        "name": "genexus_refactor",
        "arguments": {
            "action": "ExtractSubroutine",
            "target": "ProcAutoVarTest",
            "code": "&NewCounter = 42",
            "subroutineName": "ResetCounter",
            "dryRun": False
        }
    }
}
resp, _ = post_rpc(refactor_req, session_id)
print("ExtractSubroutine response:", resp)

read_source_req = {
    "jsonrpc": "2.0",
    "id": 8,
    "method": "tools/call",
    "params": {
        "name": "genexus_read",
        "arguments": {
            "name": "ProcAutoVarTest",
            "part": "Source"
        }
    }
}
resp, _ = post_rpc(read_source_req, session_id)
text_source = resp.get("result", {}).get("content", [{}])[0].get("text", "")
print("Updated Source:\n", text_source)

print("\n--- 4. Test Item D: Transfer Export with includeDependencies ---")
xpz_path = r"C:\Projetos\Genexus18MCP\scratchpad\test_export.xpz"
if os.path.exists(xpz_path):
    os.remove(xpz_path)

transfer_req = {
    "jsonrpc": "2.0",
    "id": 9,
    "method": "tools/call",
    "params": {
        "name": "genexus_transfer",
        "arguments": {
            "action": "export",
            "targets": ["ProcAutoVarTest"],
            "includeDependencies": True,
            "outputFile": xpz_path
        }
    }
}
resp, _ = post_rpc(transfer_req, session_id)
print("Transfer export response:", resp)
print("XPZ file exists:", os.path.exists(xpz_path))
if os.path.exists(xpz_path):
    print("XPZ file size:", os.path.getsize(xpz_path), "bytes")

# Clean up
print("\n--- Clean up ---")
post_rpc({
    "jsonrpc": "2.0",
    "id": 10,
    "method": "tools/call",
    "params": {
        "name": "genexus_delete_object",
        "arguments": {"name": "ProcAutoVarTest", "confirm": True}
    }
}, session_id)
if os.path.exists(xpz_path):
    os.remove(xpz_path)
print("Clean up completed.")
