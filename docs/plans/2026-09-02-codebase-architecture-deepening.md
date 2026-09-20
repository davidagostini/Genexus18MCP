# Codebase Architecture Deepening Implementation Plan

> **For Agentic Assistants:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Deepen the 5 architectural modules of Genexus18MCP across 3 sequenced phases, replacing shallow wrappers, stubs, and orphaned abstractions with true depth, high locality, and in-memory test surfaces.

**Architecture:**
- **Phase 1 (Worker Mutation Foundation):** Deepen `MutationEngine` into the single authoritative mutation module (`IMutationEngine`), encapsulating preflight validation (`TextPayloadGuard`), optimistic concurrency checking (`expectedVersion`), dry-run preview diff generation, multi-object atomic staging (`MultiObjectUnitOfWork`), and automated LIFO rollback compensation (`EditSnapshotStore`). Deepen `VisualSurfaceDomain` with real `WebFormVisualSurface` and `ReportVisualSurface` adapters, retiring dummy stubs in `IVisualSurfaceAdapter`.
- **Phase 2 (Worker Read & Lifecycle):** Deepen `ObjectReader` and activate `PartSerializerRegistry` with typed part serializers, while unifying query parsing with `QueryGrammar`. Deepen `CompilationPipeline` with `EnvironmentScope` and dual build runners.
- **Phase 3 (Gateway Orchestration):** Wire `McpMiddlewarePipeline` into `Program.RequestLoop.cs`, decomposing the 2,746-line procedural request loop into isolated, unit-testable middleware stages and eliminating mutable static globals.

**Tech Stack:** C#, .NET 8.0 (`GxMcp.Gateway`), .NET Framework 4.8 STA (`GxMcp.Worker`), GeneXus 18 SDK (`Artech.*`), xUnit.

---

## Phase 1: Worker Mutation Foundation

### Task 1: Deepen `MutationEngine` into the Authoritative Unit-of-Work Module

**Files:**
- Modify: `src/GxMcp.Worker/Services/MutationEngine.cs`
- Modify: `src/GxMcp.Worker/Helpers/MultiObjectUnitOfWork.cs`
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs`
- Test: `src/GxMcp.Worker.Tests/Services/MutationEngineTests.cs`

**Step 1: Write failing tests in `src/GxMcp.Worker.Tests/Services/MutationEngineTests.cs`**
- Test 1: `MutationEngine_RejectsLiteralLineBreaks_BeforeTouchingStorage` (validates `TextPayloadGuard` preflight).
- Test 2: `MutationEngine_RejectsOptimisticConcurrencyConflict` (validates `expectedVersion` check).
- Test 3: `MutationEngine_GeneratesDryRunDiffPlan_WithoutPersisting` (validates in-memory preview without COM calls).
- Test 4: `MutationEngine_MultiObjectUnitOfWork_ExecutesLifoRollbackOnFailure` (validates reverse-order compensation).

**Step 2: Run test to verify failure**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~MutationEngineTests"`
Expected: FAIL (missing features / methods)

**Step 3: Implement deep `MutationEngine`**
- Introduce `IMutationEngine` interface and `ISdkObjectWriter` adapter seam.
- Implement pre-flight checks inside `MutationEngine`:
  - `TextPayloadGuard` validation across all text parts.
  - Optimistic concurrency guard comparing `expectedVersion` with current snapshot hash.
  - Dry-run simulation returning full diff plan without mutating KB state.
- Absorb `MultiObjectUnitOfWork` into `MutationEngine.Stage` and `MutationEngine.Commit`.
- Implement automated LIFO rollback compensation using `EditSnapshotStore` snapshots and write a diagnostic delta to the worker log directory upon unexpected failure.
- Wire `CommandDispatcher.cs` to route `genexus_edit`, `genexus_create`, `genexus_refactor`, and batch writes through `IMutationEngine`.

**Step 4: Run tests to verify they pass**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~MutationEngineTests"`
Expected: PASS

**Step 5: Verify existing WriteService tests pass**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~WriteService"`
Expected: PASS (all 73+ tests pass)

**Step 6: Commit**
```bash
git add src/GxMcp.Worker/Services/MutationEngine.cs src/GxMcp.Worker/Helpers/MultiObjectUnitOfWork.cs src/GxMcp.Worker/Services/CommandDispatcher.cs src/GxMcp.Worker.Tests/Services/MutationEngineTests.cs
git commit -m "feat(worker): deepen MutationEngine into authoritative unit-of-work module with LIFO rollback"
```

---

### Task 2: Deepen `VisualSurfaceDomain` and Retire Dummy Stubs

**Files:**
- Modify: `src/GxMcp.Worker/Services/IVisualSurfaceAdapter.cs`
- Modify: `src/GxMcp.Worker/Services/LayoutService.cs`
- Modify: `src/GxMcp.Worker/Helpers/ReportLayoutHelper.cs`
- Modify: `src/GxMcp.Worker/Helpers/WebFormXmlHelper.cs`
- Test: `src/GxMcp.Worker.Tests/VisualSurfaceDomainTests.cs`

**Step 1: Write failing tests in `VisualSurfaceDomainTests.cs`**
- Test 1: `WebFormVisualSurface_ProjectsVisualTree_PreservesUntouchedControls`
- Test 2: `ReportVisualSurface_DiffPlan_PreservesUntouchedPrintBlocksAndColors`
- Test 3: `VisualSurface_NormalizesColorTokens_AcrossDotNetAndGeneXusFormats`

**Step 2: Run test to verify failure**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~VisualSurfaceDomainTests"`
Expected: FAIL

**Step 3: Implement deep `VisualSurfaceDomain`**
- Replace stub implementations in `IVisualSurfaceAdapter.cs` with real adapters:
  - `WebFormVisualSurface`: Parses native WebForm parts, calculates delta against `baselineXml`, and applies updates.
  - `ReportVisualSurface`: Implements baseline XML diffing, untouched print block preservation, and control creation.
- Centralize semantic color equivalence (`ColorHelper` + `XmlEquivalence`) in the visual adapters to prevent Issue #122 regressions.
- Route visual writes from `LayoutService` through `VisualSurfaceDomain`.

**Step 4: Run tests to verify they pass**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~VisualSurfaceDomainTests"`
Expected: PASS

**Step 5: Commit**
```bash
git add src/GxMcp.Worker/Services/IVisualSurfaceAdapter.cs src/GxMcp.Worker/Services/LayoutService.cs src/GxMcp.Worker/Helpers/ReportLayoutHelper.cs src/GxMcp.Worker/Helpers/WebFormXmlHelper.cs src/GxMcp.Worker.Tests/VisualSurfaceDomainTests.cs
git commit -m "feat(worker): deepen VisualSurfaceDomain with baseline diffing and semantic color equivalence"
```

---

## Phase 2: Worker Read & Lifecycle

### Task 3: Deepen `ObjectReader` & Activate `PartSerializerRegistry`

**Files:**
- Modify: `src/GxMcp.Worker/Structure/PartSerializerRegistry.cs`
- Modify: `src/GxMcp.Worker/Services/ObjectInspectionModule.cs`
- Modify: `src/GxMcp.Worker/Helpers/QueryGrammar.cs`
- Modify: `src/GxMcp.Worker/Services/SearchService.cs`
- Modify: `src/GxMcp.Worker/Services/ListService.cs`
- Test: `src/GxMcp.Worker.Tests/PartSerializerRegistryTests.cs`

**Step 1: Write failing tests in `PartSerializerRegistryTests.cs`**
- Test concrete serializers: `SourcePartSerializer`, `VariablesPartSerializer`, `WebFormPartSerializer`, `DataSelectorPartSerializer`.
- Test `QueryGrammar` parsing of complex filters (`type:Procedure parent:Root`).

**Step 2: Run test to verify failure**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~PartSerializerRegistryTests"`

**Step 3: Implement concrete serializers and wire `QueryGrammar`**
- Populate `PartSerializerRegistry` with typed serializers extracted from `ObjectService.ReadObjectSourceInternal`.
- Wire `QueryGrammar` into `SearchService` and `ListService`, replacing duplicated regexes.

**Step 4: Run tests to verify they pass**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~PartSerializerRegistryTests"`
Expected: PASS

**Step 5: Commit**
```bash
git add src/GxMcp.Worker/Structure/PartSerializerRegistry.cs src/GxMcp.Worker/Services/ObjectInspectionModule.cs src/GxMcp.Worker/Helpers/QueryGrammar.cs src/GxMcp.Worker/Services/SearchService.cs src/GxMcp.Worker/Services/ListService.cs src/GxMcp.Worker.Tests/PartSerializerRegistryTests.cs
git commit -m "feat(worker): activate PartSerializerRegistry and unify query parsing with QueryGrammar"
```

---

### Task 4: Deepen `CompilationPipeline` with `EnvironmentScope`

**Files:**
- Modify: `src/GxMcp.Worker/Services/CompilationPipeline.cs`
- Modify: `src/GxMcp.Worker/Services/InProcessBuildRunner.cs`
- Modify: `src/GxMcp.Worker/Services/BuildService.cs`
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs`
- Test: `src/GxMcp.Worker.Tests/CompilationPipelineTests.cs`

**Step 1: Write failing tests in `CompilationPipelineTests.cs`**
- Test 1: `EnvironmentScope_RestoresOriginalEnvironment_OnSuccessOrFailure`
- Test 2: `CompilationPipeline_HarvestsDiagnostics_FromStructuredLog`

**Step 2: Run test to verify failure**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~CompilationPipelineTests"`

**Step 3: Implement `EnvironmentScope` and wire `CompilationPipeline`**
- Implement `EnvironmentScope` as a disposable context in `CompilationPipeline` ensuring active model/environment is reverted on disposal.
- Encapsulate execution strategies (`InProcessBuildRunner` and external MSBuild process runner) behind internal `IBuildRunner` seam.
- Route lifecycle build and specify actions in `CommandDispatcher` through `CompilationPipeline`.

**Step 4: Run tests to verify they pass**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~CompilationPipelineTests"`
Expected: PASS

**Step 5: Commit**
```bash
git add src/GxMcp.Worker/Services/CompilationPipeline.cs src/GxMcp.Worker/Services/InProcessBuildRunner.cs src/GxMcp.Worker/Services/BuildService.cs src/GxMcp.Worker/Services/CommandDispatcher.cs src/GxMcp.Worker.Tests/CompilationPipelineTests.cs
git commit -m "feat(worker): deepen CompilationPipeline with EnvironmentScope and unified diagnostic harvesting"
```

---

## Phase 3: Gateway Orchestration

### Task 5: Wire `McpMiddlewarePipeline` into Gateway Request Loop

**Files:**
- Modify: `src/GxMcp.Gateway/Pipelines/McpMiddlewarePipeline.cs`
- Modify: `src/GxMcp.Gateway/Program.RequestLoop.cs`
- Modify: `src/GxMcp.Gateway/DiagnosticAndHealingEngine.cs`
- Test: `src/GxMcp.Gateway.Tests/Pipelines/McpMiddlewarePipelineTests.cs`

**Step 1: Write failing tests in `McpMiddlewarePipelineTests.cs`**
- Test 1: `MiddlewarePipeline_ExecutesStagesSequentially`
- Test 2: `MiddlewarePipeline_ShortCircuits_OnValidationError`
- Test 3: `MiddlewarePipeline_ServesCachedResponse_OnIdempotencyMatch`

**Step 2: Run test to verify failure**
Run: `dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~McpMiddlewarePipelineTests"`

**Step 3: Implement concrete middleware stages and wire into `ProcessMcpRequest`**
- Implement stages: `ProtocolHandshakeMiddleware`, `KbResolutionMiddleware`, `ArgsValidationMiddleware`, `IdempotencyMiddleware`, `SemanticCacheMiddleware`, `WorkerDispatchMiddleware`, `ResponseShapingMiddleware`.
- Wire `DiagnosticAndHealingEngine` into `genexus_doctor` and unhandled worker error handlers.
- Delegate `Program.RequestLoop.cs` to `McpMiddlewarePipeline.ExecuteAsync`.

**Step 4: Run Gateway test suite**
Run: `dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj`
Expected: All tests pass.

**Step 5: Commit**
```bash
git add src/GxMcp.Gateway/Pipelines/McpMiddlewarePipeline.cs src/GxMcp.Gateway/Program.RequestLoop.cs src/GxMcp.Gateway/DiagnosticAndHealingEngine.cs src/GxMcp.Gateway.Tests/Pipelines/McpMiddlewarePipelineTests.cs
git commit -m "feat(gateway): wire McpMiddlewarePipeline to decompose procedural request loop"
```

---

## Final Validation & Review

**Step 1: Run complete repository test suites**
```powershell
dotnet test Genexus18MCP.sln
npm test
npm run lint
```
Expected: 0 errors, all test suites green.

**Step 2: Update `CHANGELOG.md` under `## Unreleased`**
- Document all 5 deepened modules under `### Added` and `### Changed`.
