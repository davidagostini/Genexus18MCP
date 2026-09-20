# MCP Performance Optimizations Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement comprehensive performance improvements across the 4 key MCP layers: (1) Token and tool profile efficiency in discovery, (2) Gateway streaming serialization and allocation reduction, (3) Offload JSON parsing and validation from the Worker STA thread, and (4) Worker LOH compaction and zero-copy cache access, followed by an end-to-end benchmark comparison.

**Architecture:**
- Gateway (.NET 10): Ensure `tools/list` serves cached zero-alloc tool profiles and eliminate intermediate string allocations in `WorkerProcess` IPC by streaming JSON directly to the pipe writer via `JsonTextWriter`.
- Worker (.NET Framework 4.8 STA): Offload JSON parsing from the single-threaded STA apartment by passing pre-parsed `SdkCommandItem` through `SdkCommandQueue`, keeping the STA thread dedicated strictly to COM SDK execution. Refine idle LOH compaction and cache key generation.
- Benchmarks: Execute Gateway and Worker benchmarks before and after to quantify gains in latency, Gen0 collections, and throughput.

**Tech Stack:** C# .NET 10 (Gateway), C# .NET Framework 4.8 (Worker), Newtonsoft.Json, xUnit.

---

### Task 1: Token & Tool Profile Efficiency in Gateway

**Files:**
- Modify: `src/GxMcp.Gateway/ToolProfileFilter.cs`
- Modify: `src/GxMcp.Gateway/McpRouter.cs`
- Test: `src/GxMcp.Gateway.Tests/ToolProfileBenchmarkTests.cs`

**Step 1: Verify existing tool profile tests pass**

Run:
```powershell
dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~ToolProfile"
```
Expected: PASS.

**Step 2: Ensure cached profiles avoid repeated array creation and support fast-path profiles**

In `src/GxMcp.Gateway/ToolProfileFilter.cs`, verify `GetOrCreateFiltered` caches each normalized profile token combination and returns immutable cached arrays directly to `McpRouter`.

**Step 3: Run tests to verify correctness**

Run:
```powershell
dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~ToolProfile"
```
Expected: PASS.

---

### Task 2: Gateway Streaming Serialization & Zero-Alloc Pipe Communication

**Files:**
- Modify: `src/GxMcp.Gateway/WorkerProcess.cs`
- Test: `src/GxMcp.Gateway.Tests/PayloadSerializationBenchmarkTests.cs`
- Test: `src/GxMcp.Gateway.Tests/WorkerProcessTests.cs`

**Step 1: Check existing serialization tests**

Run:
```powershell
dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~PayloadSerialization"
```
Expected: PASS.

**Step 2: Optimize WorkerProcess pipe writer to stream JSON directly**

In `src/GxMcp.Gateway/WorkerProcess.cs`, inspect `ProcessQueueAsync`. Instead of `rpc.ToString(Formatting.None)` which allocates large strings in the Gen0/LOH before writing, use direct streaming or pooled writing to `_pipeWriter` using `JsonTextWriter(new StreamWriter(_pipeWriter))` while maintaining the single-line invariant.

**Step 3: Run tests to verify pipe communication integrity**

Run:
```powershell
dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~WorkerProcess|FullyQualifiedName~PayloadSerialization"
```
Expected: PASS.

---

### Task 3: Offload JSON Parsing from the Worker STA Thread

**Files:**
- Modify: `src/GxMcp.Worker/Program.cs`
- Test: `src/GxMcp.Worker.Tests/WorkerPerformanceBenchmarkTests.cs`

**Step 1: Run worker benchmark and dispatcher tests**

Run:
```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~WorkerPerformance|FullyQualifiedName~Dispatcher"
```
Expected: PASS.

**Step 2: Define `SdkCommandItem` and eliminate redundant `JObject.Parse` in `DrainSdkCommands`**

In `src/GxMcp.Worker/Program.cs`:
1. Define struct `internal struct SdkCommandItem { public JObject Obj; public string RawLine; }`.
2. Update `SdkCommandQueue` to `BlockingCollection<SdkCommandItem>`.
3. In the main loop where `cmdObj = TryParseCommand(line)` is already performed:
   - If not thread-safe, pass `new SdkCommandItem { Obj = cmdObj, RawLine = line }` to `EnqueueSdkCommand`.
4. In `DrainSdkCommands()`:
   - Take `SdkCommandItem item`.
   - Call `ProcessCommand(item.Obj, item.RawLine)` directly without re-parsing `line` on the STA thread.

**Step 3: Run worker tests to verify no regressions**

Run:
```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~WorkerPerformance|FullyQualifiedName~Dispatcher"
```
Expected: PASS.

---

### Task 4: Proactive Memory Maintenance & Cache Access Optimization

**Files:**
- Modify: `src/GxMcp.Worker/Program.cs`
- Modify: `src/GxMcp.Worker/Services/ObjectService.cs`
- Test: `src/GxMcp.Worker.Tests/ObjectServiceBenchmarkTests.cs`

**Step 1: Check ObjectService benchmarks**

Run:
```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~ObjectService"
```
Expected: PASS.

**Step 2: Review and tune LOH compaction trigger in `IdleMemoryMaintenance` and verify cache lookups**

In `src/GxMcp.Worker/Program.cs`:
- Check `IdleMemoryMaintenance` interval and trigger condition to ensure it reclaims fragmented LOH after heavy operations without causing latency spikes during active work.
In `src/GxMcp.Worker/Services/ObjectService.cs`:
- Ensure `BuildReadCacheKey` and cache lookups remain zero-allocation and bypass unnecessary string interpolations.

**Step 3: Run worker tests**

Run:
```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~ObjectService"
```
Expected: PASS.

---

### Task 5: Final Benchmark Execution & Comparison Report

**Step 1: Run Gateway benchmarks**

Run:
```powershell
dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~Benchmark" --logger "console;verbosity=normal"
```

**Step 2: Run Worker benchmarks**

Run:
```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~Benchmark" --logger "console;verbosity=normal"
```

**Step 3: Document before vs after comparison in `docs/benchmarks/2026-09-13-performance-baseline.md`**
