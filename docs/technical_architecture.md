# GeneXus MCP Technical Architecture

This document describes the current runtime architecture. It is not a historical roadmap.

## Runtime model

The system uses a dual-process design:

- `GxMcp.Gateway` on .NET 8 owns the MCP protocol surface.
- `GxMcp.Worker` on .NET Framework 4.8 owns GeneXus SDK execution.

```mermaid
graph LR
    A[Client] -->|stdio or HTTP /mcp| B[Gateway]
    B -->|worker RPC| C[Worker]
    C -->|Artech SDK| D[Knowledge Base]
```

## Transport

### Official transports

- stdio MCP
- HTTP MCP at `/mcp`

### HTTP behavior

- `initialize` is required before tool or resource calls.
- Requests must use `MCP-Protocol-Version: 2025-11-25`.
- Session-aware HTTP uses `MCP-Session-Id`.
- `GET /mcp` is used for SSE notifications.
- `DELETE /mcp` closes the session.

## Discovery-first surface

Clients are expected to discover capabilities dynamically:

- `tools/list`
- `resources/list`
- `resources/templates/list`
- `prompts/list`
- `completion/complete`

The extension uses this MCP discovery flow directly.

## Worker responsibilities

- Open and manage the active KB
- Read and write object parts
- Execute analysis, refactor, formatting, lifecycle, history, structure, and property operations
- Isolate GeneXus runtime constraints from the gateway process

### Text patch responsibilities

The `genexus_edit mode=patch` implementation keeps its external contract in
`PatchService`, but separates deterministic work from SDK side effects:

- `PatchTextEditor` owns pure context matching, Replace, InsertAfter, fuzzy
  matching, diagnostics, and edit-distance calculations.
- `PatchService` orchestrates snapshots, optimistic concurrency, the single
  write attempt, forced post-save reads, cache invalidation, and rollback.
- `PatchPersistenceReceipt` builds the stable persistence evidence (`saved`,
  `verified`, hashes, old-context presence, and rollback status).
- `TextPersistenceVerifier` owns exact, normalized, and semantic equivalence.

These boundaries are internal. They must not change tool arguments, response
codes, write count, transaction behavior, or invoke any KB lifecycle action.

## Gateway responsibilities

- MCP routing
- HTTP session lifecycle
- Worker lifecycle and restart boundaries
- Dynamic tool publication from `tool_definitions.json`
- Resource, prompt, and completion exposure
- Additive response normalization for `tools/call` payloads (`mcp-axi/2` metadata under `_meta` and lightweight aggregates)

## Tool call response contract (gateway-normalized)

For MCP `tools/call`, the gateway returns standard MCP `content[].text`, but normalizes JSON payloads with additive metadata:

- `_meta.schemaVersion = "mcp-axi/2"` (v2.0.0; field is underscore-prefixed per MCP convention)
- `_meta.tool = <tool-name>`
- collection helpers when inferable: `returned`, `total`, `empty`, `hasMore`, `nextOffset`
- truncation hints: `_meta.truncated=true` plus actionable `help` message
- v2.0.0 fields: `_meta.idempotent` (cache hit), `_meta.batched` (`targets[]`), `_meta.dryRun` (preview), `_meta.removedTools` (advertised on `initialize`)
- idempotent marker: `noChange=true` when the worker reports successful no-op

Compatibility rules:

- Existing worker fields are preserved.
- Enrichment is additive and does not change no-args launcher behavior.
- Clients that ignore new fields remain compatible.

## Design constraints

- New features should target MCP tools, resources, prompts, or completion endpoints.
- New extension flows must only target MCP contracts.
- Resource reads should be preferred for stable browsable context.
- Large object reads should be paginated with `genexus_read` or coordinated with `genexus_read(targets=[...])` (plural form, v2.0.0+; the legacy `genexus_batch_read` was removed).
