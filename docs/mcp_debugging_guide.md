# GeneXus MCP Debugging Guide

This guide documents how to debug the current MCP-first runtime.

## Runtime shape

- Client or extension talks MCP to the gateway.
- Gateway talks to the worker.
- Worker talks to the GeneXus SDK and KB.

## Primary checks

### HTTP MCP sanity

Validate against `/mcp`.

Required baseline:

- `MCP-Protocol-Version: 2025-11-25`
- POST `Content-Type: application/json` and `Accept: application/json, text/event-stream`
- SSE `GET` requests must include `Accept: text/event-stream`
- `initialize` before other MCP requests
- `MCP-Session-Id` reused after initialization

Typical flow:

1. `initialize`
2. `tools/list`
3. `resources/list`
4. `tools/call`

### stdio sanity

When launching the gateway as a stdio MCP server:

- stdout must remain reserved for protocol messages
- logs belong on stderr
- the process must stay idle without printing banner text

### Recover after a closed client transport

If a client-owned STDIO process exits, the client may keep reporting
`Transport closed` even though the Gateway HTTP endpoint is healthy. A dead
STDIO stream cannot carry a command that repairs itself. Keep the existing
conversation/history and use the out-of-band HTTP client:

```powershell
.\scripts\mcp_recover.ps1 `
  -BaseUrl http://127.0.0.1:5000/mcp `
  -Tool genexus_whoami
```

The script creates a fresh MCP session, discovers the live tool catalog, and
blocks every tool not explicitly annotated read-only. After reviewing a
mutating request, pass `-AllowWrite` to opt in. The Gateway must already be
running at `BaseUrl`; process supervision remains the deployment's
responsibility.

## Common failure modes

### Invalid JSON-RPC id handling

Preserve the original JSON type of `id` in responses. Converting a numeric `id` into a string breaks clients even when the payload looks correct.

### Session misuse

If `/mcp` is returning protocol errors after initialization, verify that the client is reusing the correct `MCP-Session-Id`.

### Protocol-version mismatch

If initialization fails, verify `MCP-Protocol-Version: 2025-11-25`.

### Worker startup failure

If discovery works but `tools/call` fails, inspect worker startup and GeneXus SDK loading. The gateway can initialize without a healthy worker, but execution calls cannot succeed.

### Long-running tool timeout with operation tracking

When a tool exceeds the gateway timeout budget, the request may continue in the worker.

The timeout error now includes:

- `operationId`
- `correlationId`

Use:

- `genexus_lifecycle(action='status', target='op:<operationId>')`
- `genexus_lifecycle(action='result', target='op:<operationId>')`

Automated smoke script:

- `powershell -ExecutionPolicy Bypass -File scripts/mcp_smoke.ps1`

You can also stream status via SSE (`GET /mcp`) and listen for `notifications/message` entries emitted by the gateway.

### Patch ambiguity and no-match diagnostics

For `genexus_edit(mode='patch')`, the worker now emits explicit patch statuses:

- `Applied`
- `NoChange`
- `NoMatch`
- `Ambiguous`
- `Error`

Prefer checking `patchStatus` and `details` before retrying with larger payload changes.

### AXI-style MCP tool payload metadata

`tools/call` responses may now include additive gateway metadata inside the JSON text payload:

- `_meta.schemaVersion` (current: `mcp-axi/2`, bumped in v2.0.0; field is underscore-prefixed per MCP convention)
- `_meta.tool`
- list helpers (`returned`, `total`, `empty`, `hasMore`, `nextOffset`) when inferable
- truncation signal (`_meta.truncated=true`) and contextual `help` hints
- v2.0.0 fields: `_meta.idempotent`, `_meta.batched`, `_meta.dryRun`, `_meta.removedTools` (see `docs/llm_cli_mcp_playbook.md`)

If a response from `genexus_query` or `genexus_list_objects` is missing fields such as `description`, `parent`, or other metadata, the most likely cause is the compact projection being applied. `axiCompact` defaults to **`true`** for both tools, returning only `name`, `type`, and `path` (plus `parentPath` for `genexus_list_objects`).

### Field projection and compact mode

`genexus_query` and `genexus_list_objects` apply compact projection by default:

- To get the full payload (description, parent, metadata, etc.), pass `axiCompact: false` explicitly.
- To receive a custom subset of fields, use the `fields` parameter (array or comma-separated string).

These options keep token volume low while preserving protocol compatibility. If you are here because a field is missing, adding `axiCompact: false` to the call is the first thing to try.

### Save fallback diagnostics

When source-part saves use fallback strategy (`object_save_only`), this is surfaced in response metadata (`retryStrategy`) and gateway metrics.

Fetch aggregate metrics with:

- `genexus_lifecycle(action='status', target='gateway:metrics')`

## What changed from the old model

- HTTP MCP is active and official.
- The gateway HTTP surface is `/mcp` only.
- Nexus-IDE and current clients should be debugged through the MCP session flow.
