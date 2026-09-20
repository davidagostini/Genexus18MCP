# Multi-KB Context Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make multi-KB selection deterministic per MCP session, self-identifying in tool responses, and compatible with legacy and current OpenCode MCP configuration shapes while preserving the other harnesses.

**Architecture:** Keep `Environment.DefaultKb` as the persisted startup fallback and treat `ActiveKb` as a compatibility alias only. Snapshot the fallback into the MCP session at initialize, allow `set_default` to update the current session plus the persisted fallback, and never let `open` silently change selection. Resolve calls in the order explicit `kb` → session selection → persisted fallback → single-open fallback → ambiguity. Emit the resolved alias in both the MCP result metadata and the JSON payload consumed by stdio clients.

**Tech Stack:** C#/.NET 8 Gateway, Newtonsoft.Json, xUnit, Node.js test runner, OpenCode JSON/JSONC MCP configuration.

---

### Task 1: Lock session-scoped resolution semantics

**Files:**
- Modify: `src/GxMcp.Gateway/KbResolver.cs`
- Modify: `src/GxMcp.Gateway.Tests/KbResolverTests.cs`
- Create: `src/GxMcp.Gateway/SessionKbContextStore.cs`
- Create: `src/GxMcp.Gateway.Tests/SessionKbContextStoreTests.cs`

- [ ] Add tests proving a session selection wins over the persisted default, an explicit alias wins over both, and a closed/invalid selection does not silently fall through to another open KB.
- [ ] Add tests proving session entries are isolated by session id and can be cleared after `close`.
- [ ] Add the optional `sessionDefaultAlias` input to `KbResolver.Resolve`; preserve existing overloads for callers that do not have a session.
- [ ] Add a bounded session context store with separate support for the long-lived stdio session and expiring HTTP sessions.

### Task 2: Integrate selection into Gateway lifecycle

**Files:**
- Modify: `src/GxMcp.Gateway/Program.cs`
- Modify: `src/GxMcp.Gateway/Program.RequestLoop.cs`
- Modify: `src/GxMcp.Gateway/Program.Whoami.cs`
- Modify: `src/GxMcp.Gateway/HttpSessionState.cs`
- Modify: `src/GxMcp.Gateway.Tests/WhoamiVersionTests.cs`
- Modify: `src/GxMcp.Gateway.Tests/SuggestedNextStepTests.cs`

- [ ] Snapshot the configured default when an MCP session initializes.
- [ ] Pass that session selection to KB resolution without mutating it for an ordinary explicit `kb` call.
- [ ] Make `set_default` update the current session and the persisted startup default.
- [ ] Make `open` only acquire/register a worker; return `selected=false` unless the session already selected that alias.
- [ ] Clear a session selection when its KB is closed.
- [ ] Expose `selectedKb`, `defaultKb`, and `activeKb` distinctly in `genexus_kb list` and `genexus_whoami`.

### Task 3: Make responses self-identifying

**Files:**
- Modify: `src/GxMcp.Gateway/Program.ToolPayload.cs`
- Create: `src/GxMcp.Gateway.Tests/KbResponseMetadataTests.cs`

- [ ] Add `kbAlias` to KB-bound normalized payloads so legacy OpenCode stdio text consumers can correlate responses.
- [ ] Add the same alias under the MCP result `_meta` field for clients that preserve protocol metadata.
- [ ] Leave gateway-only/meta-tool responses unchanged unless they already expose KB context.
- [ ] Cover object and array payloads, compact projections, and errors.

### Task 4: Support OpenCode legacy and v2 MCP configuration

**Files:**
- Modify: `cli/lib/config.js`
- Modify: `cli/run.test.js`
- Modify: `README.md`
- Modify: `CHANGELOG.md`

- [ ] Preserve the existing direct `mcp.genexus` shape used by OpenCode 1.x.
- [ ] Detect and update `mcp.servers.genexus` when the existing config uses the v2 nested shape, using `disabled:false` instead of the legacy `enabled:true`.
- [ ] Read and remove either shape, preserve unrelated MCP entries, and continue using `npx.cmd`/`GX_CONFIG_PATH` on Windows.
- [ ] Add fixture-style CLI tests for both shapes and verify the other harness formats remain unchanged.
- [ ] Document the OpenCode session workflow: initialize, inspect `whoami`, use `set_default` for the session, and pass explicit `kb` for cross-KB calls.

### Task 5: Verify the complete contract

**Files:**
- Modify: `src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json` only if the schema description changes.

- [ ] Run focused Gateway tests for resolver, context store, response metadata, and whoami.
- [ ] Run full Gateway tests, full CLI tests, build, JSON validation, and `git diff --check`.
- [ ] Run an isolated OpenCode `mcp list`/configuration smoke using a scratch config when the installed binary exits non-interactively.
- [ ] Review the final diff for unrelated changes and report any remaining limitation such as OpenCode config reload requiring a new process.
