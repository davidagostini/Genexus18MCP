# Close Seven Open Repository Issues Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve and locally validate the seven currently open repository issues (#134–#140) without publishing a release or closing GitHub issues.
**Architecture:** Keep the existing schema as the MCP contract source, derive action classification from explicit per-tool read/mutate maps, preserve OpenCode CLI auto-registration while making OpenCode Desktop manual setup precise and non-destructive, and document the remaining live-KB/manual gates honestly.
**Tech Stack:** C#/.NET 8 and .NET Framework 4.8, xUnit, Newtonsoft.Json, Node.js/npm, Markdown, GitHub CLI.

---

## 1. Establish the contract and dependency baseline

- [x] Re-read the applicable repository instructions and confirm the working tree is clean before edits.
- [x] Capture the seven issue requirements, current `npm audit` findings, Release warning counts, and existing focused test commands in the working notes.
- [x] Keep the plan and changelog updates scoped to this task; do not commit, push, release, or close issues.

## 2. Fix #134 — vulnerable transitive npm dependencies

**Files:** `package-lock.json`, `CHANGELOG.md`.

- [x] Refresh only the lockfile-resolved transitive versions for `brace-expansion` and `js-yaml` using the declared npm dependency graph.
- [x] Verify `npm audit` reports no vulnerabilities, then run `npm test` and `npm run lint`.
- [x] Add an Unreleased changelog entry describing the dependency remediation.

## 3. Fix #136 — legacy `genexus_analyze mode=explain` envelope

**Files:** `src/GxMcp.Gateway/tool_definitions.json`, `src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json`, `src/GxMcp.Gateway.Tests/ToolDefinitionsRedirectsTests.cs`, `CHANGELOG.md`.

- [x] Add `explain` to the schema enum and description while keeping the existing router/worker `NotImplemented` behavior.
- [x] Regenerate the discovery golden fixture through `GXMCP_UPDATE_GOLDEN=1` and verify its ordering/parity.
- [x] Invert the stale regression test so it requires schema acceptance and preserves the `NotImplemented` contract test.
- [x] Add an Unreleased changelog entry for the compatibility correction.

## 4. Fix #137 — deterministic no-navigation live smoke test

**Files:** `src/GxMcp.Gateway.Tests/E2ELiveSmokeTests.cs`, `CHANGELOG.md`.

- [x] Paginate the Procedure listing until exhaustion (or a bounded safety cap) instead of sampling the first five rows.
- [x] Continue past list/indexing/error responses and navigation reports with missing levels; retain observed names/statuses for a useful failure message.
- [x] Assert `NoNavigationBlocks` and its hint only after a valid empty-level report is found.
- [x] Add an Unreleased changelog entry and run the live test filter when a `GXMCP_TEST_KB` is available; otherwise record the skip explicitly.

## 5. Fix #139 — complete explicit action classification

**Files:** `src/GxMcp.Gateway/OperationClassifier.cs`, `src/GxMcp.Gateway.Tests/OperationClassifierTests.cs`, `src/GxMcp.Gateway.Tests/ToolActionContractTests.cs` (new), `CHANGELOG.md`.

- [x] Replace permissive “anything not known-mutating is read-only” branches with explicit read-only and mutating action sets for every action-bearing schema tool.
- [x] Treat omitted and unknown actions as non-read-only; preserve `dryRun` as read-only only for a known mutating action that explicitly supports preview semantics.
- [x] Add a schema-driven parity test proving every action enum value has a classification and a regression matrix for omitted/unknown actions and representative writes/previews.
- [x] Add an Unreleased changelog entry.

## 6. Fix #140 — help/catalog/inventory parity

**Files:** `src/GxMcp.Gateway/ToolHelpCatalog.cs`, `src/GxMcp.Gateway.Tests/ToolActionContractTests.cs`, `docs/mcp_capabilities_inventory.md`, `README.md`, `CHANGELOG.md`.

- [x] Add detailed help entries for all action-bearing tools missing from `ToolHelpCatalog`.
- [x] Add a machine-checkable action inventory table listing all 31 umbrella tools, every valid action, and read-only versus mutating behavior.
- [x] Test schema-to-help coverage and inventory-to-schema/action parity so future action additions cannot silently drift.
- [x] Correct the public README tool count and the `genexus_doc` dependency-graph description.
- [x] Document the real-KB validation gate for the homonymous `genexus_structure action=get_visual` route without claiming CI coverage that does not exist.
- [x] Add an Unreleased changelog entry.

## 7. Fix #135 — OpenCode Desktop setup path

**Files:** `cli/lib/config.js`, `cli/commands/axi.js` if output shaping needs adjustment, `cli/run.test.js`, `README.md`, `CHANGELOG.md`.

- [x] Keep Desktop detection read-only and distinguish `manual` setup from CLI auto-registration in structured client status.
- [x] Return exact command, arguments, environment variable, restart, and validation guidance; never write the undocumented Desktop-managed `mcp.json` format.
- [x] Add tests for Desktop detection/status and `clients add` reporting a manual skip while preserving unrelated configuration.
- [x] Add a documented OpenCode Desktop section and an Unreleased changelog entry.

## 8. Fix #138 — Release warning baseline and reduction

**Files:** `src/GxMcp.Benchmarks/SearchRankParallelismBenchmark.cs`, `src/GxMcp.Gateway.Tests/LifecycleResponseShaperTests.cs`, `src/GxMcp.Gateway.Tests/RecipeCatalogTests.cs`, `src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj`, `docs/build_warning_baseline.md`, `CHANGELOG.md`.

- [x] Initialize or correctly nullable-annotate benchmark model properties and nullable xUnit inputs to remove the actionable compiler/analyzer warnings.
- [x] Align the Worker test project’s known SDK reference-conflict suppression with the already-scoped Worker project suppression, with a narrowly documented reason.
- [x] Rebuild Release from scratch, count warnings by project/code, and record the post-change baseline plus the residual-warning policy in a dedicated document.
- [x] Add an Unreleased changelog entry describing the baseline and reductions.

## 9. Full validation and handoff

- [x] Run focused C# tests for schema, classifier, help, router, and live-test compilation.
- [x] Run `npm audit`, `npm test`, `npm run lint`, `dotnet build Genexus18MCP.sln -c Release`, and `dotnet test Genexus18MCP.sln` with the required GeneXus SDK path.
- [x] Run the live navigation/explain tests if the environment exposes `GXMCP_TEST_KB`; otherwise report them as skipped and retain the manual validation gate.
- [x] Inspect the final diff/status for scope, contracts, warning counts, tests, docs, and residual risks.
- [x] Report completed fixes, validation results, residual live/release gates, and explicitly leave GitHub issue closure/release for authorized follow-up.
