# PR #133 Quality and Release Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Integrate the typed Transaction-record capability safely, close the confirmed quality gaps, measure practical performance risks, and release the combined changes only after fresh validation.

**Architecture:** Keep the current `main` contracts as the source of truth. Update the PR branch in an isolated worktree, preserving the explicit schema-driven action classifier and adding record actions to the gateway, help, inventory, and tests. Keep direct SQL bounded and receipt-protected; do not broaden the feature into an unrelated refactor.

**Tech Stack:** C#/.NET 8 gateway, .NET Framework 4.8 Worker, Node.js CLI, xUnit, SQL Server test seam, GitHub Actions, PowerShell release script.

---

### Task 1: Prepare isolated PR integration

**Files:**
- Create: `docs/superpowers/plans/2026-09-04-pr-133-quality-release.md`
- Modify: none in source code

- [x] **Step 1: Record the implementation plan**

Run: `git status --short --branch` and record the clean `main` baseline before creating the feature worktree.

- [x] **Step 2: Confirm the PR merge base and current checks**

Run: `gh pr view 133 --json baseRefOid,headRefOid,mergeable,mergeStateStatus,statusCheckRollup` and `git merge-tree origin/main origin/pr-133`.

Expected: the PR is based on an older `main`, has four known textual conflicts, and its existing green check is not a validation of the current `main`.

- [ ] **Step 3: Create a feature worktree from the PR head**

Run:

```powershell
git fetch origin pull/133/head:refs/remotes/origin/pr-133
git worktree add -b codex/pr-133-quality worktrees/pr-133-quality origin/pr-133
```

Expected: `worktrees/pr-133-quality` is on a new local branch containing the PR head; the original `main` worktree remains clean.

### Task 2: Harden live navigation smoke behavior

**Files:**
- Modify: `src/GxMcp.Gateway.Tests/E2ELiveSmokeTests.cs:140-215`
- Test: `src/GxMcp.Gateway.Tests/E2ELiveSmokeTests.cs`

- [ ] **Step 1: Add regression coverage for bounded transient pagination**

Add a testable helper around the live response state machine and cover these exact cases: `IndexNotReady` retries within the cap, a partial page with `hasMore=true` and no usable offset retries or fails with a diagnostic at the cap, and an empty page terminates only after a valid exhausted response.

- [ ] **Step 2: Run the focused test before the implementation**

Run: `dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~E2ELiveSmokeTests" --no-restore`.

Expected: the new regression cases fail against the current terminal-on-empty/error logic.

- [ ] **Step 3: Implement the bounded state machine**

Use explicit `sort: "name"`, a fixed maximum number of attempts/pages, and separate handling for retryable indexing/partial responses versus unrecoverable tool errors. Continue only with a validated forward `nextOffset`; stop on a valid exhausted response or report the exact response state when the cap is reached.

- [ ] **Step 4: Require an explicit no-navigation report**

Treat `levels: []` as exhaustion only when the response is non-error, has `status: "NoNavigationBlocks"`, and carries the expected hint. Empty or malformed levels in an error response must remain a failure.

- [ ] **Step 5: Run the focused test after the implementation**

Run the same focused command and require zero failures. When `GXMCP_TEST_KB` is unset, verify the suite reports the documented skip rather than claiming a live smoke result.

### Task 3: Make action classification conservative and internally consistent

**Files:**
- Modify: `src/GxMcp.Gateway/OperationClassifier.cs`
- Modify: `src/GxMcp.Gateway.Tests/ToolActionContractTests.cs`
- Modify: `src/GxMcp.Gateway.Tests/SemanticCacheInvalidationTests.cs`
- Modify: `docs/mcp_capabilities_inventory.md`
- Modify: `src/GxMcp.Gateway/ToolHelpCatalog.cs`
- Test: `src/GxMcp.Gateway.Tests/OperationClassifierTests.cs` or the existing classifier test file

- [ ] **Step 1: Add failing classifier cases**

Cover exact ordinal action matching, omitted actions, wrong-cased actions, and parameter-dependent side effects for transfer export, navigation cache updates, browser build/screenshot/baseline options, and the three Transaction-record actions.

- [ ] **Step 2: Run classifier and contract tests before the implementation**

Run: `dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~OperationClassifier|FullyQualifiedName~ToolActionContract|FullyQualifiedName~SemanticCacheInvalidation" --no-restore`.

Expected: the new cases expose case-insensitive classification, missing record-action integration, or filesystem/cache side-effect ambiguity.

- [ ] **Step 3: Enforce exact token matching and record-action parity**

Use `StringComparison.Ordinal` for action tokens. Add `records_query` as read-only, `records_insert` and `records_update` as mutating, and add all write-capable record actions to the current dry-run capability set. Keep unknown or omitted actions conservative.

- [ ] **Step 4: Align side-effect semantics**

Either classify parameter-dependent filesystem/cache mutations as mutating/unknown or explicitly narrow the classifier contract to KB mutation everywhere. The selected contract must match tool annotations, help, inventory, and cache invalidation tests; no parameter that writes an artifact may be described as purely read-only.

- [ ] **Step 5: Run the focused tests after the implementation**

Require all focused classifier, contract, and cache tests to pass before moving on.

### Task 4: Make explicit OpenCode Desktop setup actionable

**Files:**
- Modify: `cli/lib/config.js`
- Modify: `cli/run.test.js`

- [ ] **Step 1: Add failing tests for undetected explicit setup**

Cover `clients add --clients opencode-desktop` with no detected marker and with a stale or missing `GENEXUS_MCP_GATEWAY_EXE`. The result must still contain manual setup instructions and must not be blocked by launcher validation that applies only to writable clients.

- [ ] **Step 2: Run the focused CLI tests before the implementation**

Run: `npm run test:one -- "OpenCode Desktop"`.

Expected: the undetected explicit-client cases fail because the current path can return no actionable result.

- [ ] **Step 3: Return structured manual setup for every explicit detect-only request**

Always emit the known platform path/marker status and manual configuration instructions for an explicitly selected OpenCode Desktop target. Restrict gateway executable validation to clients that actually write a launcher configuration; preserve unrelated client behavior.

- [ ] **Step 4: Make platform scope explicit**

Use platform-specific paths and markers where supported, or return a clear Windows-only status on other platforms. Add the corresponding test without changing detected-client write behavior.

- [ ] **Step 5: Run the full CLI test and lint checks**

Run: `npm test` and `npm run lint`.

Expected: all CLI tests pass and lint exits 0.

### Task 5: Enforce schema/help/inventory parity and clean compatibility documentation

**Files:**
- Modify: `src/GxMcp.Gateway.Tests/ToolActionContractTests.cs`
- Modify: `src/GxMcp.Gateway/ToolHelpCatalog.cs`
- Modify: `docs/mcp_capabilities_inventory.md`
- Modify: `src/GxMcp.Worker/Services/AnalyzeService.cs`
- Modify: `src/GxMcp.Gateway.Tests/ToolDefinitionsRedirectsTests.cs`
- Modify: `src/GxMcp.Gateway/tool_definitions.json`
- Modify: `src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json`

- [ ] **Step 1: Add failing exact-parity assertions**

Parse schema action tokens, inventory action rows, and structured help metadata. Assert equality in both directions, reject duplicate inventory rows, and require each action’s read-only/mutating and dry-run behavior to be represented.

- [ ] **Step 2: Run the contract tests before the implementation**

Run: `dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~ToolActionContract|FullyQualifiedName~ToolDefinitionsRedirects" --no-restore`.

Expected: current incomplete help/inventory and contradictory `explain` comments are detected.

- [ ] **Step 3: Update published help and inventory**

Document `records_query`, `records_insert`, and `records_update`, including dry-run, receipt, direct-database, and business-rule boundaries. Keep the current `main` schema budget and action descriptions when integrating the PR.

- [ ] **Step 4: Correct legacy explain comments**

Describe `genexus_analyze mode=explain` as an accepted compatibility mode that returns a typed `NotImplemented` response; do not describe it as removed from the public schema.

- [ ] **Step 5: Run the contract tests after the implementation**

Require exact parity tests and discovery fixture checks to pass.

### Task 6: Review Transaction-record performance and scope

**Files:**
- Modify: `src/GxMcp.Worker/Services/TransactionRecordsService.cs` only if measurement identifies a safe bottleneck
- Modify: `src/GxMcp.Worker.Tests/TransactionRecordsServiceTests.cs`
- Review: `docs/transaction-records-benchmarks.md`
- Review: `src/GxMcp.Worker/Services/WriteService.cs`

- [ ] **Step 1: Establish bounded-query and allocation baselines**

Run the existing Transaction-record service tests and benchmark/diagnostic commands from `docs/transaction-records-benchmarks.md`. Record query count, row limits, receipt operations, and elapsed time for query, preview, and write paths using the existing fake database seam.

- [ ] **Step 2: Inspect the hot path before changing it**

Trace metadata resolution, SQL construction, connection discovery, transaction snapshot, verification, commit, and independent reread. Keep one metadata read per operation where possible, preserve `TOP` bounds, and avoid any client-controlled identifier interpolation.

- [ ] **Step 3: Add a regression test for the chosen performance invariant**

Assert that a bounded query never reads beyond the requested limit, previews do not open a write transaction, and a persisted write performs only the documented snapshot/write/verify/reread sequence. Do not optimize based on wall-clock numbers from the fake provider alone.

- [ ] **Step 4: Separate or justify unrelated WriteService behavior**

If the DataProvider object-level save/flush change is not required by typed records, move it to a separate commit/PR. If it is required, add a focused regression test and document the coupling in the changelog.

- [ ] **Step 5: Run Worker-focused tests and build**

Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~TransactionRecords" --no-restore` and `dotnet build src/GxMcp.Worker/GxMcp.Worker.csproj -v:minimal` with `GX_PATH` set.

Expected: all focused tests pass and the Worker build exits 0.

### Task 7: Rebase, review, merge, and release

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `docs/release_protocol.md` only if the implementation reveals a durable protocol gap

- [ ] **Step 1: Rebase the feature branch onto current `origin/main`**

Resolve conflicts in `CHANGELOG.md`, `ToolSchemaSizeTests.cs`, `OperationClassifier.cs`, and `tool_definitions.json` by combining the PR feature with the current #134–#140 contracts. Preserve CRLF and do not discard unrelated main changes.

- [ ] **Step 2: Run complete validation on the rebased branch**

Run:

```powershell
npm audit --json
npm test
npm run lint
dotnet test Genexus18MCP.sln
git diff --check origin/main...HEAD
```

Also run the release build with `GX_PATH` and the controlled live smoke when `GXMCP_TEST_KB` is configured. A missing live KB must be reported as an explicit unvalidated gate, not as success.

- [ ] **Step 3: Request independent code review**

Dispatch a read-only reviewer with the exact base/head SHAs, requirements, changed-file scope, test output, and performance evidence. Fix all Critical/Important findings before merge.

- [ ] **Step 4: Push the updated PR branch and wait for fresh CI**

Push only the explicit PR branch ref and verify the new workflow run is green against the current `main`.

- [ ] **Step 5: Merge PR #133**

Use the repository’s normal GitHub merge operation only after `mergeable` is clean and required checks are green. Verify `origin/main` contains the rebased PR commits.

- [ ] **Step 6: Prepare and publish the release**

Add the exact release version entry to `CHANGELOG.md`, run `.\release.ps1 -Version 2.57.0`, and verify the GitHub release workflow plus `npm view genexus-mcp@latest version`.

- [ ] **Step 7: Verify the final repository state**

Run `git status --short --branch`, `gh pr view 133 --json state,mergeStateStatus`, `gh run list --workflow release.yml`, and `npm view genexus-mcp@latest version`. Report any live-KB limitation or registry propagation delay explicitly.
