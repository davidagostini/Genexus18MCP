# Canonical envelope coverage

This is the P0 response-contract audit for the published MCP surface.

## Scope and method

- Source of truth: `src/GxMcp.Gateway/tool_definitions.json`.
- Dispatch path: Gateway router → Worker `CommandDispatcher` → service.
- Run `python scripts/validate-tool-contracts.py` for the current tool and action inventory; counts are derived from the schema rather than duplicated here.
- `EnvelopeContractGuardTests` statically guards Worker service response shapes against legacy helpers and top-level status literals. It does not prove that every computed runtime branch has been exercised. Runtime coverage remains the responsibility of `EnvelopeConformance` and live-KB tests.

## Contract by response family

| Family | Canonical status | Required contract | Current state | Follow-up |
| --- | --- | --- | --- | --- |
| Read/query/list | `ok` or `partial` | Payload under `result`; collection metadata where applicable | Migrated helper usage is dominant | P0.2 map outliers in runtime contract tests |
| Successful mutation | `ok` | Stable `code`, target, result, persistence evidence when applicable | Migrated helper usage is dominant | Require `changed`/verification semantics per mutation family |
| No-op mutation | `ok` with code `NoChange` | No legacy top-level `noChange` field | Covered by canonical helpers and guards | Add cross-family runtime assertions |
| Validation or partial result | `partial` | `result` plus `warnings[]` | Canonical partial helper; runtime cases covered separately | Ensure warnings have stable machine-readable codes |
| Long-running operation | `accepted` | `operationId`, preferably `pollTarget`, `pollTool`, `cancelTool` | Canonical accepted helper; Gateway also tracks operations | Normalize all async entry points |
| Client/input failure | `error` | `error.code`, `error.message`, actionable `hint`/`nextSteps` | Broad helper coverage | Complete action-by-action next-step audit |
| Transient failure | `error` | `retryable: true`, normally `retryAfterMs` | Helper now supports explicit decision | Migrate transient branches to set it explicitly |
| Unknown commit state | `error` | `retryable: false`, `reconciliationRequired: true`, operation handle | Journal and operation tracker exist | P0.3 adds the complete reconciliation path |
| Gateway/protocol failure | JSON-RPC error or transport result | Stable protocol code and correlation metadata | Implemented in Gateway paths | Align with worker envelope where a tool call is still represented |

## Known boundaries

The following statuses are valid inside domain sub-payloads and are not envelope statuses by themselves: build state, index state, health state, preview state, and version-control state. They must remain under `result` or a dedicated nested object. Gateway operation tracker states (`Running`, `Completed`, `Failed`, `Cancelled`) are lifecycle state and must not be confused with the worker response `status` enum.

Some Gateway helper paths intentionally return operational objects such as `NotFound`, `Rejected`, `Blocked`, or `TrackingLost`. These are not worker tool envelopes and require separate Gateway contract coverage rather than being silently renamed.

## Migration rules

1. New Worker responses use only `McpResponse.Ok`, `Err`, `Partial`, or `Accepted`.
2. Tool payload belongs under `result`; error details belong under `error`.
3. Every error branch declares a stable code and, where a safe action exists, a `nextSteps[]` entry.
4. Retry and reconciliation decisions are explicit booleans; clients must not infer them from message text.
5. Async operations expose one stable `operationId` and a lifecycle target.
6. Any intentional Gateway-only response shape gets a focused contract test and is documented here.

## Verification

The narrow contract suite validates the helper surface, malformed optional fields, legacy-shape rejection, runtime envelopes, and accepted operations:

```text
dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~EnvelopeContractGuardTests|FullyQualifiedName~McpResponseExtensionsTests|FullyQualifiedName~ServiceRuntimeEnvelopeTests" --no-restore -v:minimal
```

The next task is P0.3: make the unknown-commit state and reconciliation behavior complete and externally actionable.
