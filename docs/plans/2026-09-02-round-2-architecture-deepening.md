# Round 2 Architecture Deepening Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Deepen four high-friction, unaddressed subsystems of Genexus18MCP (Gateway Worker Supervision, Worker Index Storage & Graph Deconstruction, Schema Mutation Engine, and CLI Multi-Format Client Config Adapters) by establishing deep interfaces with high locality and testable seams.

**Architecture:** Decompose the gateway's fragmented worker management into a deep `WorkerSupervisor` with an `IWorkerTransport` seam; decompose the worker's 2,167-line `IndexCacheService` into an isolated `IndexStorageEngine` and `IKbGraphIndex` while connecting `MemoryService` to `VectorService`; unify structural snapshotting, dry-run diffs, and rollback into `SchemaMutationEngine`; and transform the 1,500-line procedural `cli/lib/config.js` into a polymorphic `ClientAdapter` strategy hierarchy.

**Tech Stack:** C# (.NET 8 for Gateway, .NET Framework 4.8 for Worker), xUnit, Named Pipes IPC, Node.js (v18+) for CLI.

---

### Task 1: Gateway Worker Supervisor & Transport Seam (Candidate 1)

**Files:**
- Create: `src/GxMcp.Gateway/WorkerSupervisor.cs`
- Create: `src/GxMcp.Gateway.Tests/WorkerSupervisorTests.cs`
- Modify: `src/GxMcp.Gateway/WorkerPool.cs`
- Modify: `src/GxMcp.Gateway/Program.WorkerLifecycle.cs`

**Step 1: Write the failing test**
Create `src/GxMcp.Gateway.Tests/WorkerSupervisorTests.cs` testing:
1. `WorkerSupervisor_AcquireAsync_AcquiresNewWorker_ViaTransportSeam`: demonstrates acquiring worker using an in-memory transport seam without real OS process.
2. `WorkerSupervisor_CrashRecovery_ExecutesBackoffAndRetry`: verifies that when the transport drops unexpectedly, the supervisor triggers fast-retry with backoff.
3. `WorkerSupervisor_CapacityEviction_EvictsOldestWorker_WhenMaxReached`: verifies LRU eviction policy when `MaxOpenKbs` is exceeded.

**Step 2: Run test to verify it fails**
Run: `dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~WorkerSupervisorTests"`

**Step 3: Implement minimal code to pass**
- Define `IWorkerTransport` and `IWorkerProcessHandle` interfaces in `src/GxMcp.Gateway/WorkerSupervisor.cs`.
- Implement `WorkerSupervisor` encapsulating `ConcurrentDictionary<string, Entry>`, per-KB `SemaphoreSlim` spawn gates, LRU eviction (`SelectVictim`), and eager crash-respawn backoff logic.
- Connect `WorkerPool` to delegate to `WorkerSupervisor`.

**Step 4: Run test to verify it passes**
Run: `dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~WorkerSupervisorTests"`

---

### Task 2: Worker Index Storage Engine & Graph Seam (Candidate 2)

**Files:**
- Create: `src/GxMcp.Worker/Services/IndexStorageEngine.cs`
- Create: `src/GxMcp.Worker.Tests/IndexStorageEngineTests.cs`
- Modify: `src/GxMcp.Worker/Services/IndexCacheService.cs`
- Modify: `src/GxMcp.Worker/Services/MemoryService.cs`

**Step 1: Write the failing test**
Create `src/GxMcp.Worker.Tests/IndexStorageEngineTests.cs` testing:
1. `IndexStorageEngine_Sharding_ComputesStableShardBuckets`: verifies FNV-1a 32-bit stable hashing across storage keys.
2. `IndexStorageEngine_FlushAndLoad_PreservesShardedPayload`: tests saving and reloading dirty shards to a temporary directory with GZip compression.
3. `MemoryService_Recall_EnrichesWithVectorSimilarity`: tests fact recall utilizing `VectorService` cosine similarity ranking.

**Step 2: Run test to verify it fails**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~IndexStorageEngineTests"`

**Step 3: Implement minimal code to pass**
- Implement `IIndexStorageEngine` and `IndexStorageEngine` in `src/GxMcp.Worker/Services/IndexStorageEngine.cs` extracting the 16-shard FNV-1a hashing, GZip stream serialization, dirty generation counters, and manifest validation from `IndexCacheService.cs`.
- Refactor `IndexCacheService.cs` to delegate disk persistence to `IIndexStorageEngine`.
- Wire `VectorService` into `MemoryService` for semantic ranking on fact recall.

**Step 4: Run test to verify it passes**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~IndexStorageEngineTests"`

---

### Task 3: Schema Mutation Engine & Relational Advisory Wiring (Candidate 4)

**Files:**
- Create: `src/GxMcp.Worker/Services/Structure/SchemaMutationEngine.cs`
- Create: `src/GxMcp.Worker.Tests/SchemaMutationEngineTests.cs`
- Modify: `src/GxMcp.Worker/Services/StructureService.cs`
- Modify: `src/GxMcp.Worker/Services/DbOptimizeService.cs`

**Step 1: Write the failing test**
Create `src/GxMcp.Worker.Tests/SchemaMutationEngineTests.cs` testing:
1. `SchemaMutationEngine_DryRun_ComputesSchemaDiffWithoutMutating`: verifies preview diff generation without mutating the underlying object.
2. `SchemaMutationEngine_Failure_RollsBackToLosslessSnapshot`: verifies that when an operation fails mid-mutation, the original state is restored.
3. `DbOptimizeService_ProducesExecutableIndexPlan`: verifies index advisor recommendations can be converted to an `IndexCreatePlan`.

**Step 2: Run test to verify it fails**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~SchemaMutationEngineTests"`

**Step 3: Implement minimal code to pass**
- Implement `ISchemaMutationEngine` and `SchemaMutationEngine` in `src/GxMcp.Worker/Services/Structure/SchemaMutationEngine.cs` encapsulating version token validation, lossless snapshot capture, and compensation/rollback.
- Rewire `StructureService.cs` to delegate transaction/structure mutations through `SchemaMutationEngine`.
- Connect `DbOptimizeService` index suggestions to `IndexMutationPlanner.Create`.

**Step 4: Run test to verify it passes**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~SchemaMutationEngineTests"`

---

### Task 4: CLI Polymorphic Client Config Adapters (Candidate 5)

**Files:**
- Create: `cli/lib/client-adapters.js`
- Create: `cli/lib/client-adapters.test.js`
- Modify: `cli/lib/config.js`

**Step 1: Write the failing test**
Create `cli/lib/client-adapters.test.js` testing:
1. `McpServersJsonAdapter_appliesAndRemovesEntry`: tests atomic mutation and backup of standard `mcpServers` JSON files.
2. `OpenCodeJsoncAdapter_preservesCommentsAndNesting`: tests JSONC comment preservation and handling of `mcp.servers` vs `mcp`.
3. `CodexTomlAdapter_parsesAndStripsTomlBlocks`: tests TOML block extraction, serialization, and removal without corrupting surrounding sections.

**Step 2: Run test to verify it fails**
Run: `node --test cli/lib/client-adapters.test.js`

**Step 3: Implement minimal code to pass**
- Implement `ClientAdapter` base class and concrete adapters (`McpServersJsonAdapter`, `VsCodeServersAdapter`, `OpenCodeJsoncAdapter`, `CodexTomlAdapter`) in `cli/lib/client-adapters.js`.
- Refactor `applyClientEntry` and `removeClientEntry` in `cli/lib/config.js` to delegate to the strategy adapters.

**Step 4: Run test to verify it passes**
Run: `node --test cli/lib/client-adapters.test.js`

---

### Task 5: Full Verification, Documentation & CHANGELOG

**Step 1: Run comprehensive tests**
- `dotnet test Genexus18MCP.sln`
- `npm test`
- `npm run lint`

**Step 2: Record in CHANGELOG.md**
Add entries under `## Unreleased` detailing the new deep modules.
