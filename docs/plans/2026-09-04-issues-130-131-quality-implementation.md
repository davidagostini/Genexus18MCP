# Implementation Plan: Issues #130, #131 and PR #132 Integration

**Goal:** Implement all open PRs (#132) and issues (#130 and #131) in Genexus18MCP with maximum quality, addressing operational defects (dryRun in index, semantic cache invalidation, crash retry safety, classifier alignment, homonym resolution), contract schema synchronization, comprehensive documentation, and robust automated regression tests.

**Architecture:**
- **PR #132 Integration**: Keep fast-forward merged branch with enhanced CI resilience, ephemeral port race retry in live smoke harness, robust abort handling in IDE client, and typed route persistence.
- **Issue #130 (AGENTS.md & Harness Sync)**: Add explicit preflight rules, decision matrix, side effects/safety boundaries, checkout-scoped process management, and portable placeholders to `AGENTS.md`, cross-referencing playbook and release protocol.
- **Issue #131 (Multi-Action Contracts & Defects)**:
  - *Phase A (P0 Defects)*: Pass `dryRun` in `SystemRouter.cs` for `lifecycle index`; register `remove_attribute` in `Program.ToolPayload.cs` (`IsMutatingTool`); guard `RetrySafeReadTools` in `Program.WorkerLifecycle.cs` with action-awareness for multi-action tools; synchronize internal read-only classifiers (`MacroSuggestionService`, `NextLegalActionsBuilder`) removing dead tools (`genexus_logs`, `genexus_history`) and aligning multi-action behavior.
  - *Phase B (P1 Homonyms - Empresa case)*: Add `type` disambiguator to `genexus_structure` across Gateway router, schema, and Worker `StructureService.GetVisualStructure`. Surface resolved object type when falling back to unsupported type errors.
  - *Phase C (P2 Effect Metadata & ToolHelpCatalog)*: Align annotations and description of `genexus_doc` (set `readOnlyHint=false`, `idempotentHint=false`, fix sequence diagrams -> dependency graph). Extend `ToolHelpCatalog` with comprehensive guides for `genexus_structure`, `genexus_layout`, `genexus_versioning`, `genexus_io`, `genexus_kb_version`, `genexus_doc`, `genexus_recipe`, and `genexus_refactor`.
  - *Phase D (P3 Contract Tests & Schema)*: Remove dead action `run` from `genexus_recipe` enum and hint; declare missing parameters in schema (`part`, `page`, `pageSize`, `notifyOnFailure`, `skipFullDeploy`, `fastIncremental` in `genexus_lifecycle`; `steps` in `genexus_recipe`; `type` in `genexus_structure`); add contract tests for undeclared parameters and router handlers.
  - *Phase E (P4 Descriptions)*: Enrich descriptions across all multi-action schemas (`genexus_layout`, `genexus_db`, `genexus_create`, `genexus_versioning`, `genexus_io`, `genexus_kb_version`, `genexus_edit_form`, `genexus_security`, `genexus_apply_pattern`, `genexus_refactor`, `genexus_gxserver`, `genexus_structure`). Synchronize `tools-list.response.json`.
  - *Phase F (Derived Docs & CHANGELOG)*: Update `docs/mcp_capabilities_inventory.md`, `README.md`, `docs/agent_playbook.md`, and log all entries in `CHANGELOG.md` under `## Unreleased`.

**Tech Stack:** .NET 8 (Gateway, C#), .NET Framework 4.8 (Worker, C#), Node.js (CLI / Nexus IDE), xUnit, Git.

---

## Tasks Breakdown

### Task 1: PR #132 Verification & Baseline
- Already fast-forward merged on `main`.
- Verify tests on Gateway, Worker, and CLI.

### Task 2: Issue #130 — AGENTS.md Workflow & Harness Sync
- Add decision matrix and preflight rules to `AGENTS.md`.
- Replace hardcoded `C:\Projetos\Genexus18MCP` with repository-root placeholder.
- Document OpenCode detect-only and installer parameters.

### Task 3: Issue #131 Phase A — P0 Operational Defects
- **A1**: Propagate `dryRun` in `SystemRouter.cs` for `action="index"`. Add test in `LifecycleRouterDryRunTests.cs`.
- **A2**: Add `remove_attribute` to `Program.ToolPayload.cs` under `genexus_structure` in `IsMutatingTool`. Add regression test in `SemanticCacheInvalidationTests.cs`.
- **A3**: Update `ShouldRetryWorkerCrash` in `Program.WorkerLifecycle.cs` to check action-level safety for `genexus_structure` (and any other multi-action tools). Add test in Gateway tests.
- **A4**: Reconcile `MacroSuggestionService.ReadOnlyTools` and `NextLegalActionsBuilder._readOnlyTools`: remove `genexus_logs` and `genexus_history`, add action-level classification for `genexus_telemetry`, `genexus_versioning`, `genexus_recipe`, `genexus_doc`, `genexus_kb`. Add tests.

### Task 4: Issue #131 Phase B — P1 Homonym Resolution (Empresa Case)
- In `StructureService.cs`: update `GetVisualStructure` to accept `typeFilter`.
- In `CommandDispatcher.cs`: parse `type` for `GetVisualStructure`.
- In `OperationsRouter.cs`: forward `type` in `ConvertStructureToolCall`.
- In `StructureService.cs`: format error with resolved type and hint when untyped resolution hits an unsupported object type.
- Add regression tests in `GxMcp.Worker.Tests`.

### Task 5: Issue #131 Phase C — P2 Effect Metadata & ToolHelpCatalog Extension
- In `tool_definitions.json`: update `genexus_doc` annotations (`readOnlyHint=false`, `idempotentHint=false`) and description ("dependency graph").
- Extend `ToolHelpCatalog.cs` to cover `genexus_structure`, `genexus_layout`, `genexus_versioning`, `genexus_io`, `genexus_kb_version`, `genexus_doc`, `genexus_recipe`, and `genexus_refactor`.
- Synchronize golden fixture `tools-list.response.json`.

### Task 6: Issue #131 Phase D & E — Schema Completeness & Contract Tests
- Remove `action="run"` from `genexus_recipe` enum and error hint.
- Add missing properties to `tool_definitions.json`:
  - `genexus_lifecycle`: `part`, `page`, `pageSize`, `notifyOnFailure`, `skipFullDeploy`, `fastIncremental`.
  - `genexus_recipe`: `steps`.
  - `genexus_structure`: `type`.
- Enrich property descriptions for `genexus_layout`, `genexus_db`, `genexus_create`, `genexus_versioning`, `genexus_io`, `genexus_kb_version`, `genexus_edit_form`, `genexus_security`, `genexus_apply_pattern`, `genexus_refactor`, `genexus_gxserver`, `genexus_structure`.
- Add contract test in `GxMcp.Gateway.Tests` ensuring no router-consumed parameters are missing from `tool_definitions.json`.
- Synchronize `tools-list.response.json`.

### Task 7: Issue #131 Phase F — Derived Docs, CHANGELOG & Final Validation
- Update `docs/mcp_capabilities_inventory.md`, `README.md`, `docs/agent_playbook.md`.
- Add comprehensive entries to `CHANGELOG.md` under `## Unreleased`.
- Run full test suite: `dotnet test Genexus18MCP.sln`, `npm test`, `npm run lint`.
