# Events Object Save Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `requireObjectSave` fail closed for stale writes, report metadata evidence only when the persistence stamp succeeds, reject unsupported full-mode usage, and verify the result against a disposable live KB when the harness prerequisites exist.

**Architecture:** Keep the public MCP contract in the gateway schema and help catalog, while enforcing the safety boundary in the Worker facade and patch service. The writer performs the final version check inside its per-target lock; the object-save receipt receives explicit metadata-stamp evidence instead of inferring durability from an in-memory timestamp alone. Live validation uses the repository's manifest-gated harness and never fabricates a fixture.

**Tech Stack:** C#/.NET Worker (`net48`), Newtonsoft.Json, xUnit, PowerShell live-KB harness, GeneXus 17/18 SDKs.

---

### Task 1: Lock the root causes with focused regressions

**Files:**
- Modify: `src/GxMcp.Worker.Tests/PatchObjectSaveEvidenceTests.cs`
- Modify: `src/GxMcp.Worker.Tests/WriteServiceFacadeArgsTests.cs`

- [x] **Step 1: Add receipt coverage for non-durable metadata evidence.**

  Extend `MetadataChanged`/receipt tests with equal revisions, an advanced `LastUpdate`, and `metadataStampPersisted: false`; the expected result is `metadataUpdated == false`.

- [x] **Step 2: Add facade validation coverage.**

  Exercise `requireObjectSave=true` with `mode=full` and with `part=Source`; both must return a structured unsupported-configuration error before any SDK lookup.

- [x] **Step 3: Run only the two focused test classes.**

  Run:

  ```powershell
  dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~PatchObjectSaveEvidenceTests|FullyQualifiedName~WriteServiceFacadeArgsTests" -v:minimal
  ```

  Expected: the new tests fail against the current implementation, confirming the regressions are meaningful.

### Task 2: Make required Events writes honor the version at the writer boundary

**Files:**
- Modify: `src/GxMcp.Worker/Services/PatchService.cs`
- Modify: `src/GxMcp.Worker/Services/WriteService.cs`
- Modify: `src/GxMcp.Worker.Tests/PatchObjectSaveEvidenceTests.cs`

- [x] **Step 1: Thread `baseVersion` through the direct writer call.**

  Add an optional `baseVersion` parameter to the direct `WriteObject` overload and pass it from `PatchService.ApplyPatch`.

- [x] **Step 2: Recheck inside `lock (AcquirePerTargetLock(target))`.**

  Before `WriteObjectInternal` can mutate the SDK object, call the existing fresh-token check. Return the existing structured `VersionConflict` response and do not call the internal writer when the token changed.

- [x] **Step 3: Keep the pre-write check as an optimization, not the safety boundary.**

  Preserve the existing patch-preparation check, but make the in-lock writer check authoritative. Do not add retries or rollback for a stale version.

- [x] **Step 4: Run the focused concurrency/patch tests.**

  Run:

  ```powershell
  dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~PatchObjectSaveEvidenceTests" -v:minimal
  ```

  Expected: all focused tests pass and the writer path contains the final version guard before mutation.

### Task 3: Make metadata evidence explicit and reject unsupported facade combinations

**Files:**
- Modify: `src/GxMcp.Worker/Services/WriteService.cs`
- Modify: `src/GxMcp.Worker/Services/PatchPersistenceReceipt.cs`
- Modify: `src/GxMcp.Worker/Services/PatchService.cs`
- Modify: `src/GxMcp.Worker.Tests/PatchObjectSaveEvidenceTests.cs`
- Modify: `src/GxMcp.Worker.Tests/WriteServiceFacadeArgsTests.cs`

- [x] **Step 1: Return whether revision-date persistence calls succeeded.**

  Make `StampObjectRevisionDates` return `bool`, set it only when a `SaveModelEntityDate`/`SaveVersionIndependentDate` call completes, and include that value in the transaction success result as `metadataStampPersisted`.

- [x] **Step 2: Gate metadata evidence on the explicit result.**

  Extend `PatchPersistenceReceipt.AttachObjectSaveEvidence` with `metadataStampPersisted`; neither a `LastUpdate`-only advance nor a `VersionId` change is accepted when the SDK metadata-stamp call was not confirmed.

- [x] **Step 3: Reject invalid `requireObjectSave` requests before SDK access.**

  In the facade, reject `requireObjectSave` unless `mode=patch` and `part=Events`, with a structured error matching the patch-service contract. The full-mode branch must never silently discard the flag.

- [x] **Step 4: Run focused tests again.**

  ```powershell
  dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~PatchObjectSaveEvidenceTests|FullyQualifiedName~WriteServiceFacadeArgsTests" -v:minimal
  ```

  Expected: all focused tests pass.

### Task 4: Review adjacent paths, update the changelog, and validate broadly

**Files:**
- Modify: `CHANGELOG.md`
- Review: `src/GxMcp.Gateway/tool_definitions.json`
- Review: `src/GxMcp.Gateway.Tests/Fixtures/Contract/Discovery/tools-list.response.json`
- Review: `src/GxMcp.Gateway/ToolHelpCatalog.cs`
- Review: `src/GxMcp.Worker/Services/MutationEngine.cs`
- Review: `src/GxMcp.Worker/Services/CommandDispatcher.cs`

- [x] **Step 1: Add the required `Unreleased` changelog entry.**

  Record the three safety fixes in the existing release voice under `### Fixed`.

- [x] **Step 2: Run Worker builds for every installed SDK major.**

  ```powershell
  $env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus17Trial'; dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj --no-restore -v:minimal
  $env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj --no-restore -v:minimal
  ```

- [x] **Step 3: Run the Worker suite, Gateway discovery contract, and diff checks.**

  ```powershell
  dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --no-restore -v:minimal
  dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~McpDiscoveryContractTests" -v:minimal
  git diff --check origin/main...HEAD
  ```

- [x] **Step 4: Run the live harness only with an existing verified disposable manifest.**

  Use `scripts/test-live.ps1` or `scripts/test-live-matrix.ps1` with the manifest under `scratchpad/`. If the manifest/KB is absent, record `live=unavailable` and the exact missing prerequisite; do not create or mutate an unverified KB.

- [x] **Step 5: Review the final diff and worktree.**

  Confirm only task files changed, no secrets or temporary artifacts are tracked, and the live gateway/worker processes are cleaned up.
