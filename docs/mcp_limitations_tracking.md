# MCP Limitations Tracking

This document separates historical observations from the current evidence-backed
status of MCP capabilities. A capability is not marked `DONE` merely because its
implementation or contract tests exist: live SDK/KB gates remain explicit when
this checkout has no qualifying fixture or recorded run.

## Status vocabulary

- `ACTIVE`: implemented and covered by repository contracts or deterministic tests.
- `PARTIAL`: implemented for a bounded surface, with a documented limitation or
  an uncompleted live/SDK gate.
- `UNVERIFIED_LIVE`: the repository contains the gate, but no qualifying live
  result is recorded here.
- `DONE`: the stated scope is confirmed by the cited evidence, including live
  confirmation where the item requires it.
- `BLOCKED`: a named dependency or SDK limitation prevents the next gate.

## Historical baseline (2026-03-25)

The original session record observed the following, but its workstream labels are
not current status and must not be used as evidence of completion:

- empty `genexus_read` parts were returning valid responses;
- HTTP MCP session flow covered `initialize`, `tools/list`, and `tools/call`;
- `notifications/initialized` returned `204` rather than `400`;
- `genexus_asset` was exposed for KB binary assets and templates;
- `ControleExtensaoHorasLoad` and `RelControleExtensaoHoras` source changes were
  confirmed by MCP re-read;
- WorkWithPlus visual metadata persistence was not reliable through the then
  current editing path.

The former workstream labels are retained only as historical context. The current
register below is authoritative.

## Current evidence register

| Capability | Status | Provenance / artifact | Evidence in this checkout | Owner | Next gate |
|---|---|---|---|---|---|
| Binary assets and report templates | `PARTIAL` | `2881e00`; `genexus_io` asset contract in `docs/mcp_capabilities_inventory.md:76,119` | Inventory records `asset_find`, `asset_read`, and `asset_write`; the implementation supports metadata-first reads and bounded content transfer | Worker/IO maintainer | Exercise read → header-cell write → re-read against the authoritative `.xlsx` in a real KB and record the resulting artifact/hash |
| WebForm and grid metadata editing | `PARTIAL` | `b8d1787`; `b0708ce`; `docs/mcp_capabilities_inventory.md:121,123-124` | Layout, pattern, and typed WWP paths are exposed; the inventory marks them active, while the required licensed-WorkWithPlus live smoke remains unrecorded | Worker/layout maintainer | Run the opt-in `LiveKbFact`/WWP gate against a licensed fixture and confirm persisted caption/visibility changes and rendering |
| Verified persistence after writes | `PARTIAL` | `417e6bb`; `a1129ed`; `src/GxMcp.Worker.Tests/PersistenceVerifierTests.cs`, `StructurePersistenceVerificationTests.cs` | Repository tests cover post-save verification and the inventory documents additive response contracts; this register does not claim universal live persistence | Worker/write-path maintainer | Extend or run a real-KB matrix for the remaining write families, recording object, version, read-back, and artifact evidence |
| Source-read budget discipline | `DONE` | `3a5d90a`; `docs/mcp_capabilities_inventory.md:31-46` | Inventory records source-first reads, compact projections, field selection, truncation metadata, and backward-compatible response shaping | Gateway maintainer | Re-open only if a response-budget regression is found |
| Empty-part semantics | `DONE` | `42ebe72`; `src/GxMcp.Gateway.Tests/FieldSelectionTests.cs` and the read contract tests | Empty parts are represented as valid empty content by the current read contract; no live SDK claim is needed for this protocol behavior | Gateway/worker maintainer | Re-open only if a supported empty part regresses to an error envelope |
| Repeatable MCP protocol smoke | `ACTIVE` | `dc8d8c6`; `fc6a8eb`; `scripts/mcp_smoke.ps1`; `.github/workflows/live-smoke.yml` | `McpSmokeScriptContractTests` runs the unmodified diagnostic script against an in-process gateway; the script covers initialize, session, tools/resources list, calls, and structured business errors | Gateway/CI maintainer | Keep the Windows contract test green; run the self-hosted live workflow with both required fixture inputs for KB artifact coverage |
| Pattern-awareness diagnostics | `PARTIAL` | `04d91f4`; `docs/mcp_capabilities_inventory.md:154,172-173`; `docs/wwp_pattern_investigation.md` | Pattern metadata and the visual-change playbook are exposed; repository evidence identifies authoritative pattern surfaces, but does not establish universal SDK behavior | Worker/layout maintainer | Record an end-to-end diagnostic for a representative WWP object, including source/layout/pattern origin and the authoritative edit surface |

### Live and SDK gates

The following are intentionally not reported as passed by ordinary CI:

1. A real KB containing the authoritative report-template asset, followed by a
   persisted asset edit and hash/content re-read.
2. A WorkWithPlus-licensed KB covering grid metadata read/write and rendering.
3. A representative real-KB persistence matrix across the remaining write paths.
4. The self-hosted Windows live workflow, which requires both `kb_path` and a
   verified synthetic `fixture_manifest` (`.github/workflows/live-smoke.yml:5-13`).

The repository's automated tests establish contracts and local gateway behavior;
they do not substitute for these gates. When a gate is run, record its commit,
fixture/artifact identifier, command, and result here before changing its status.

## Cross-links and operating order

- Capability definitions and action classifications: [`mcp_capabilities_inventory.md`](mcp_capabilities_inventory.md).
- Operational backlog: [`mcp_execution_backlog.md`](mcp_execution_backlog.md).
- Protocol diagnostic procedure: [`mcp_debugging_guide.md`](mcp_debugging_guide.md).
- Plans status and historical provenance: [`../plans/README.md`](../plans/README.md).

The current order is: close the binary-asset live gate; close the licensed WWP
and pattern-origin gates; expand real-KB persistence evidence; then keep protocol
smoke contracts as the regression gate. Do not convert an unverified live gate to
`DONE` because a build, lint run, or contract test passed.

## Update rule

When changing this file, keep one row per capability, preserve the cited
historical baseline, add concrete provenance and evidence, name the owner and
next gate, and update the linked plans register. Do not claim SDK or live behavior
without a recorded qualifying fixture and artifact.
