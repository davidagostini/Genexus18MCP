# Multi-KB Context Selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make multi-KB work predictable in OpenCode by honoring the selected default KB for calls that omit `kb`, preserving both gateway and CLI catalog formats, and exposing enough context for an agent to choose the right KB.

**Architecture:** `KbResolver` keeps explicit `kb` precedence, then resolves the configured default/active alias before falling back to the single-open-KB rule; only an actually unselected multi-KB state remains ambiguous. Gateway persistence updates a map or array catalog in place, while the Node CLI normalizes either representation and keeps `ActiveKb`/`DefaultKb` synchronized.

**Tech Stack:** C#/.NET 8 Gateway, Newtonsoft.Json, Node.js CLI, xUnit, Node test runner, Markdown/JSON MCP contracts.

---

### Task 1: Lock down selected-KB resolution

**Files:**
- Modify: `src/GxMcp.Gateway/KbResolver.cs`
- Test: `src/GxMcp.Gateway.Tests/KbResolverTests.cs`

- [x] **Step 1: Add a regression test** asserting that `Resolve(null, openKbs)` returns the configured default when two KBs are open.
- [x] **Step 2: Add a regression test** asserting that a configured default declared in `KBs[]` can be selected even when another KB is already open.
- [x] **Step 3: Run the focused resolver tests and verify the new tests fail with `KB_AMBIGUOUS`.
- [x] **Step 4: Implement precedence: explicit argument; open handle matching `DefaultKb`/`ActiveKb`; declared default; known default; sole open KB; existing zero-open fallback; ambiguity.
- [x] **Step 5: Run `dotnet test ... --filter FullyQualifiedName~KbResolverTests` and verify all pass.

### Task 2: Preserve gateway/CLI KB catalogs when setting the default

**Files:**
- Modify: `src/GxMcp.Gateway/Program.RequestLoop.cs`
- Create: `src/GxMcp.Gateway.Tests/KbCatalogPersistenceTests.cs`

- [x] **Step 1: Add tests for upserting a default alias into both `KBs` object/map and `KBs` array shapes without removing existing entries.
- [x] **Step 2: Run the new tests and verify the persistence helper is absent/fails.
- [x] **Step 3: Add one gateway-side helper that updates an existing alias or appends the missing alias while preserving the original JSON shape.
- [x] **Step 4: Use the helper in `genexus_kb action=set_default`, persist both `DefaultKb` and `ActiveKb`, and update the in-memory catalog consistently.
- [x] **Step 5: Run the focused gateway tests and inspect the JSON assertions.

### Task 3: Make the CLI consume both catalog formats

**Files:**
- Modify: `cli/lib/config.js`
- Test: `cli/run.test.js`

- [x] **Step 1: Add a CLI regression fixture/config with `Environment.KBs` as a gateway-style array and only `DefaultKb` set.
- [x] **Step 2: Assert `kb list` returns the real aliases and marks the default active.
- [x] **Step 3: Normalize array entries (`Alias`/`Path` or `alias`/`path`) into the CLI map before `add`, `remove`, or `switch` mutates them.
- [x] **Step 4: Make `readKbCatalog` fall back from `ActiveKb` to `DefaultKb`, and make writes clear/set both fields so the gateway and CLI agree.
- [x] **Step 5: Run the focused Node KB tests and the complete `npm test` suite.

### Task 4: Expose KB context at first contact

**Files:**
- Modify: `src/GxMcp.Gateway/Program.Whoami.cs`
- Modify: `src/GxMcp.Gateway/Program.RequestLoop.cs`
- Test: `src/GxMcp.Gateway.Tests/WhoamiVersionTests.cs`

- [x] **Step 1: Add assertions that `whoami.kb` contains the active alias and the open/declared alias lists.
- [x] **Step 2: Add `openKbs`/`knownKbs` and `activeKb` to the gateway `genexus_kb action=list` payload without removing existing fields.
- [x] **Step 3: Keep the payload lean: aliases and selection state in `whoami`; detailed process/path telemetry remains in `genexus_kb action=list`.
- [x] **Step 4: Run the focused whoami/gateway tests.

### Task 5: Align the MCP contract and documentation

**Files:**
- Modify: `src/GxMcp.Gateway/tool_definitions.json`
- Regenerate: `src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json`
- Modify: `README.md`

- [x] **Step 1: Update the `genexus_kb` description to state that `set_default` selects the implicit target for calls without `kb`, while explicit `kb` remains authoritative for cross-KB calls.
- [x] **Step 2: Regenerate the discovery golden fixture with `GXMCP_UPDATE_GOLDEN=1` and verify only the intentional contract description changes.
- [x] **Step 3: Rewrite the README resolution rules and OpenCode workflow with `whoami`/`genexus_kb list` → `set_default` → explicit `kb` for parallel calls.
- [x] **Step 4: Run discovery contract and fixture parity tests.

### Task 6: Final verification and release-facing record

**Files:**
- Modify: `CHANGELOG.md`

- [x] **Step 1: Run Gateway focused tests, CLI tests, and the relevant build.
- [x] **Step 2: Review `git diff --check` and the complete diff for unrelated changes.
- [x] **Step 3: Add a user-facing `## Unreleased` entry under `### Fixed` describing predictable default selection and safe multi-KB switching.
- [x] **Step 4: Report commands, observable results, and the residual requirement to use explicit `kb` for simultaneous cross-KB operations.
