# MCP Performance Optimizations Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Eliminate redundant allocations, tree duplications, and serialization bottlenecks across Gateway envelope projection, response truncation, DidYouMean Levenshtein calculations, and Worker list aggregations to significantly reduce response latency and GC pressure.

**Architecture:** 
1. Streamline Gateway response pipeline (`NormalizeToolPayloadForAxi` & `ProjectArrayItems`) to project array items directly without full intermediate object tree cloning.
2. Eliminate redundant `axiPayload.DeepClone()` in `BuildToolResultContent` structuredContent attachment.
3. Introduce fast structural pre-checks in `TruncateResponseIfNeeded` to avoid serializing entire response trees to string for sub-budget payloads.
4. Replace Levenshtein distance array allocations in `DidYouMean` with stackalloc spans and early length pruning.
5. Cache `IsLikelyType` lookup tables and optimize dictionary operations in Worker `ListService`.

**Tech Stack:** C# .NET 8 (Gateway & Benchmarks), C# .NET Framework 4.8 (Worker), BenchmarkDotNet, Newtonsoft.Json (JObject/JArray).

---

### Task 1: Optimize DidYouMean Levenshtein & Suggest (Allocation-Free)

**Files:**
- Modify: `src/GxMcp.Gateway/DidYouMean.cs`
- Test: `src/GxMcp.Gateway.Tests/DidYouMeanTests.cs`

**Step 1: Write/verify test cases for DidYouMean**
Run existing tests: `dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~DidYouMeanTests"`
Expected: PASS

**Step 2: Implement allocation-free Levenshtein and early length check in Suggest**
Use `stackalloc int[...]` for strings <= 128 characters, and prune candidates when `|len(a) - len(b)| > maxDistance`.

**Step 3: Run tests to verify correctness**
Run: `dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~DidYouMeanTests"`
Expected: PASS

---

### Task 2: Optimize Gateway Envelope Projection & StructuredContent Attachment

**Files:**
- Modify: `src/GxMcp.Gateway/Program.ToolPayload.cs:380-430, 630-685`
- Test: `src/GxMcp.Gateway.Tests/ProjectionLevelTests.cs`
- Test: `src/GxMcp.Gateway.Tests/GatewayBudgetTests.cs`

**Step 1: Run existing projection and budget tests**
Run: `dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~ProjectionLevelTests|FullyQualifiedName~GatewayBudgetTests"`
Expected: PASS

**Step 2: Implement direct array projection, single-lookup TotalsByType, and zero-clone structuredContent**
- When `ShouldProjectFieldsForTool(toolName)` is true and `requestedFields` are set, project the items directly into `obj[matchedKey]` without cloning the unprojected array first.
- In `ProjectArrayItems`, iterate row properties once and filter by `fields.Contains(prop.Name)` instead of scanning row properties per field.
- In `BuildTotalsByType`, use `TryGetValue` and read string values directly without unnecessary allocations.
- In `BuildToolResultContent`, assign `result["structuredContent"] = axiPayload` directly without `DeepClone()`.

**Step 3: Run projection tests to verify correctness**
Run: `dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~ProjectionLevelTests|FullyQualifiedName~GatewayBudgetTests"`
Expected: PASS

---

### Task 3: Implement Fast Structural Heuristic in TruncateResponseIfNeeded

**Files:**
- Modify: `src/GxMcp.Gateway/Program.ToolPayload.cs:11-45`
- Test: `src/GxMcp.Gateway.Tests/GatewayBudgetTests.cs`

**Step 1: Run budget tests**
Run: `dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~GatewayBudgetTests"`
Expected: PASS

**Step 2: Add fast-path heuristic to TruncateResponseIfNeeded**
Check string lengths and array counts before doing full `ToString(Formatting.None)`. If well below budget, return immediately.

**Step 3: Run budget tests to verify correctness**
Run: `dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~GatewayBudgetTests"`
Expected: PASS

---

### Task 4: Optimize Worker ListService Types & Aggregates

**Files:**
- Modify: `src/GxMcp.Worker/Services/ListService.cs`
- Test: `src/GxMcp.Worker.Tests/ListServiceTests.cs` or `src/GxMcp.Worker.Tests/TemporalListTests.cs`

**Step 1: Run list tests**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~TemporalListTests"`
Expected: PASS

**Step 2: Replace runtime array instantiation in IsLikelyType with static HashSet and optimize ComputeAggregates lookups**

**Step 3: Run list tests to verify correctness**
Run: `dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~TemporalListTests"`
Expected: PASS

---

### Task 5: End-to-End Verification & Benchmarking

**Files:**
- Run: `dotnet test Genexus18MCP.sln`
- Run: `dotnet run --project src/GxMcp.Benchmarks -c Release -- --filter *EnvelopeProjection*`
- Update: `CHANGELOG.md`

**Step 1: Run entire test solution**
Run: `dotnet test Genexus18MCP.sln`
Expected: 100% tests pass (1916+ worker, 1036+ gateway).

**Step 2: Run benchmark to measure throughput and allocation reduction**
Compare before vs after metrics.

**Step 3: Update CHANGELOG.md**
Document optimizations under `## Unreleased` -> `### Changed` / `### Internal`.
