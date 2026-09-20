# Architecture Deepening Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement the 4 architectural deepening refactors for Genexus18MCP to transform shallow modules into deep ones, improve locality, reduce switch boilerplate, and eliminate leaky seams.

**Architecture:** 
1. Deepen Mutation & Patch subsystem into an authoritative `MutationEngine` encapsulating dry-run simulation, comment preservation, and SDK persistence.
2. Deepen Build & Specification subsystem into a unified `CompilationPipeline` managing process execution, evidence extraction, and cancellation tokens.
3. Decompose monolithic `CommandDispatcher` into a lazy `CommandHandlerRegistry`.
4. Streamline Gateway router translation into a declarative dispatch seam.

**Tech Stack:** C# (.NET 8.0 for Gateway, .NET Framework 4.8 STA for Worker), MSTest / NUnit test suites.

---

### Task 1: Deepen Mutation & Patch Engine (Candidate 2)

**Files:**
- Modify: `src/GxMcp.Worker/Services/WriteService.cs`
- Modify: `src/GxMcp.Worker/Services/PatchService.cs`
- Modify: `src/GxMcp.Worker/Services/CommentOnlyPatch.cs`
- Modify: `src/GxMcp.Worker/Services/DryRunPlanBuilder.cs`
- Test: `src/GxMcp.Worker.Tests/PatchSafetyGuardTests.cs`
- Test: `src/GxMcp.Worker.Tests/CommentOnlyPatchTests.cs`

**Step 1: Write/verify regression tests for patch and comment mutation**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~Patch"`

**Step 2: Consolidate patch parsing, comment preservation, and dry-run verification inside WriteService**
Unify the mutation pipeline so invariants (comment preservation, idempotency, dry-run simulation) are handled uniformly in one deep module.

**Step 3: Run Worker tests to verify**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj`
Expected: ALL tests PASS.

**Step 4: Commit Phase 1**
```bash
git add src/GxMcp.Worker/Services/WriteService.cs src/GxMcp.Worker/Services/PatchService.cs
git commit -m "refactor(worker): deepen mutation and patch engine"
```

---

### Task 2: Deepen Build & Specification Engine (Candidate 4)

**Files:**
- Modify: `src/GxMcp.Worker/Services/BuildService.cs`
- Test: `src/GxMcp.Worker.Tests/SpecifyEvidenceTests.cs`
- Test: `src/GxMcp.Worker.Tests/BuildPlanServiceTests.cs`

**Step 1: Verify existing build & evidence tests**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~SpecifyEvidence"`

**Step 2: Encapsulate process lifecycle, evidence parsing, and cancellation tokens into a cohesive compilation runner**
Extract internal helpers in `BuildService.cs` into structured pipeline stages while preserving the public `BuildService` interface.

**Step 3: Run Worker tests to verify**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~Build"`

**Step 4: Commit Phase 2**
```bash
git add src/GxMcp.Worker/Services/BuildService.cs
git commit -m "refactor(worker): deepen build and specification execution pipeline"
```

---

### Task 3: Decompose Monolithic Worker Dispatcher (Candidate 3)

**Files:**
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs`
- Modify: `src/GxMcp.Worker/Services/IWorkerCommandHandler.cs` (create interface/registry)
- Test: `src/GxMcp.Worker.Tests/CommandDispatcherTests.cs`

**Step 1: Introduce CommandHandlerRegistry with lazy service resolution**
Create clean handler lookup without instantiating 120 services on cold startup.

**Step 2: Route core service modules through handler registry**
Refactor `CommandDispatcher.Dispatch` to delegate through registered domain handlers.

**Step 3: Run full Worker test suite**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj`

**Step 4: Commit Phase 3**
```bash
git add src/GxMcp.Worker/Services/CommandDispatcher.cs
git commit -m "refactor(worker): introduce lazy command handler registry to CommandDispatcher"
```

---

### Task 4: Streamline Gateway Router Seam (Candidate 1)

**Files:**
- Modify: `src/GxMcp.Gateway/Routers/OperationsRouter.cs`
- Modify: `src/GxMcp.Gateway/Routers/ObjectRouter.cs`
- Test: `src/GxMcp.Gateway.Tests/OperationsRouterTests.cs`
- Test: `src/GxMcp.Gateway.Tests/ToolPayloadTests.cs`

**Step 1: Simplify repetitive argument mapping cases in Gateway Routers**
Consolidate umbrella tool argument conversions using direct property passthroughs.

**Step 2: Run Gateway test suite**
Run: `dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj`

**Step 3: Run Full Solution Build and Tests**
Run: `.\build.ps1`
Run: `dotnet test Genexus18MCP.sln`

**Step 4: Commit Phase 4**
```bash
git add src/GxMcp.Gateway/Routers/
git commit -m "refactor(gateway): streamline two-tier router argument mappings"
```
