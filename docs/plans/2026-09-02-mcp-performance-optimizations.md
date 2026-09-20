# MCP Performance Optimizations Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Optimize GeneXus MCP performance across 4 fronts: (1) eliminate redundant JSON-AST parsing and tree cloning in the Worker command dispatcher, (2) cache filtered tool profile definitions to reduce discovery overhead, (3) expand ObjectService read caching to reduce redundant SDK COM round-trips across multi-turn sessions, and (4) optimize SourceSearch candidate loops and token allocations.

**Architecture:**
1. In `src/GxMcp.Worker/Services/CommandDispatcher.cs`: Replace `JObject.Parse(json)` in `IsCacheableSuccessEnvelope` with a zero-AST streaming `JsonTextReader` check for top-level `error` and non-cacheable `status`, and eliminate redundant `DeepClone()` on nested request params.
2. In `src/GxMcp.Gateway/ToolProfileFilter.cs` & `McpRouter.cs`: Cache the filtered `JArray` per profile (core, authoring, devops, ui, db) to make `tools/list` O(1) in memory and time.
3. In `src/GxMcp.Worker/Services/ObjectService.cs`: Extend `ReadCacheTtl` from 60s to 300s (5 minutes) with `GXMCP_READ_CACHE_TTL_SEC` override since write operations already perform deterministic cache invalidation.
4. In `src/GxMcp.Worker/Services/SourceSearchService.cs`: Hoist default `Scope` allocations out of per-object loops.
5. In `docs/environment_variables.md` & `CHANGELOG.md`: Document performance flags and profiles.

**Tech Stack:** C# .NET 8 (Gateway), C# .NET Framework 4.8 (Worker), Newtonsoft.Json, xUnit.

---

### Task 1: Streaming Zero-AST Validation in `IsCacheableSuccessEnvelope`

**Files:**
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs:551-566`
- Test: `src/GxMcp.Worker.Tests/DispatcherIdempotencyTests.cs`

**Step 1: Run existing tests for IsCacheableSuccessEnvelope**

Run:
```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~DispatcherIdempotencyTests"
```
Expected: PASS (all tests pass).

**Step 2: Replace full JObject.Parse with streaming JsonTextReader in IsCacheableSuccessEnvelope**

In `src/GxMcp.Worker/Services/CommandDispatcher.cs`, rewrite `IsCacheableSuccessEnvelope`:
```csharp
internal static bool IsCacheableSuccessEnvelope(string json)
{
    if (string.IsNullOrEmpty(json)) return false;
    
    try
    {
        using (var sr = new System.IO.StringReader(json))
        using (var reader = new Newtonsoft.Json.JsonTextReader(sr))
        {
            if (!reader.Read() || reader.TokenType != Newtonsoft.Json.JsonToken.StartObject)
                return false;

            int depth = 1;
            while (reader.Read())
            {
                if (reader.TokenType == Newtonsoft.Json.JsonToken.StartObject || reader.TokenType == Newtonsoft.Json.JsonToken.StartArray)
                {
                    depth++;
                }
                else if (reader.TokenType == Newtonsoft.Json.JsonToken.EndObject || reader.TokenType == Newtonsoft.Json.JsonToken.EndArray)
                {
                    depth--;
                    if (depth == 0) break;
                }
                else if (depth == 1 && reader.TokenType == Newtonsoft.Json.JsonToken.PropertyName)
                {
                    string propName = reader.Value?.ToString();
                    if (string.Equals(propName, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        reader.Read();
                        if (reader.TokenType != Newtonsoft.Json.JsonToken.Null)
                            return false;
                    }
                    else if (string.Equals(propName, "status", StringComparison.OrdinalIgnoreCase))
                    {
                        reader.Read();
                        string status = reader.Value?.ToString();
                        if (status != null && NonCacheableStatuses.Contains(status))
                            return false;
                    }
                }
            }
            return true;
        }
    }
    catch
    {
        return false;
    }
}
```

**Step 3: Re-run tests to verify correctness**

Run:
```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~DispatcherIdempotencyTests"
```
Expected: PASS (100% of idempotency tests pass, zero regressions).

---

### Task 2: Eliminate Redundant `DeepClone` on Request Params in `CommandDispatcher.DispatchInternal`

**Files:**
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs:603-612`
- Test: `src/GxMcp.Worker.Tests/DispatcherIdempotencyTests.cs`

**Step 1: Inspect lines 603-612 in `CommandDispatcher.cs`**

Currently:
```csharp
if (args != null && args["params"] is JObject innerArgs)
{
    var merged = (JObject)innerArgs.DeepClone();
    foreach (var prop in args.Properties())
    {
        if (prop.Name == "params") continue;
        if (merged[prop.Name] == null) merged[prop.Name] = prop.Value?.DeepClone();
    }
    args = merged;
}
```

**Step 2: Replace with shallow property copy into a new container**

```csharp
if (args != null && args["params"] is JObject innerArgs)
{
    var merged = new JObject();
    foreach (var prop in innerArgs.Properties())
    {
        merged[prop.Name] = prop.Value;
    }
    foreach (var prop in args.Properties())
    {
        if (prop.Name == "params") continue;
        if (merged[prop.Name] == null) merged[prop.Name] = prop.Value;
    }
    args = merged;
}
```

**Step 3: Run worker tests to verify dispatch integrity**

Run:
```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~Dispatcher"
```
Expected: PASS.

---

### Task 3: Tool Profile Caching in `ToolProfileFilter` & `McpRouter`

**Files:**
- Modify: `src/GxMcp.Gateway/ToolProfileFilter.cs`
- Modify: `src/GxMcp.Gateway/McpRouter.cs:382-402`
- Test: `src/GxMcp.Gateway.Tests/ToolProfileTests.cs`

**Step 1: Run existing ToolProfileTests**

Run:
```powershell
dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~ToolProfileTests"
```
Expected: PASS.

**Step 2: Add ConcurrentDictionary cache in `ToolProfileFilter`**

In `src/GxMcp.Gateway/ToolProfileFilter.cs`:
Add a cached filtered array dictionary:
```csharp
private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, JArray> _profileCache =
    new System.Collections.Concurrent.ConcurrentDictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);

public static void InvalidateCache()
{
    _profileCache.Clear();
}

public static JArray GetOrCreateFiltered(JArray allTools, string? profile)
{
    string key = string.IsNullOrWhiteSpace(profile) ? "all" : profile.Trim().ToLowerInvariant();
    return _profileCache.GetOrAdd(key, k => Filter(allTools, k));
}
```

In `src/GxMcp.Gateway/McpRouter.cs:381-402`:
Use `ToolProfileFilter.GetOrCreateFiltered(_toolDefinitions, activeProfile)` instead of calling `Filter` repeatedly on every `tools/list` request.

**Step 3: Run tests to verify ToolProfile caching**

Run:
```powershell
dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~ToolProfileTests"
```
Expected: PASS.

---

### Task 4: Extend `ReadCacheTtl` with Configurable Override in `ObjectService`

**Files:**
- Modify: `src/GxMcp.Worker/Services/ObjectService.cs:27-35`
- Test: `src/GxMcp.Worker.Tests/TemporalListTests.cs` or relevant worker tests

**Step 1: Inspect `ReadCacheTtl` in `ObjectService.cs`**

Currently line 32:
```csharp
private static readonly TimeSpan ReadCacheTtl = TimeSpan.FromSeconds(60);
```

**Step 2: Update `ReadCacheTtl` to 300s (5 min) default with environment variable override**

```csharp
private static readonly TimeSpan ReadCacheTtl = ResolveReadCacheTtl();

private static TimeSpan ResolveReadCacheTtl()
{
    string env = Environment.GetEnvironmentVariable("GXMCP_READ_CACHE_TTL_SEC");
    if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env.Trim(), out int sec) && sec > 0)
    {
        return TimeSpan.FromSeconds(sec);
    }
    return TimeSpan.FromMinutes(5);
}
```

**Step 3: Run worker tests to verify no regressions**

Run:
```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~ObjectService|FullyQualifiedName~Dispatcher"
```
Expected: PASS.

---

### Task 5: Hoist Allocations in `SourceSearchService`

**Files:**
- Modify: `src/GxMcp.Worker/Services/SourceSearchService.cs:237, 355`
- Test: `src/GxMcp.Worker.Tests/SourceSearchServiceTests.cs`

**Step 1: Check existing SourceSearchServiceTests**

Run:
```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~SourceSearch"
```
Expected: PASS.

**Step 2: Hoist default scope list**

In `SourceSearchService.cs`, add static readonly field:
```csharp
private static readonly List<string> DefaultScope = new List<string> { "source" };
```
Replace `c.Scope ?? new List<string> { "source" }` with `c.Scope ?? DefaultScope` across `SourceSearchService.cs` (lines 237, 355).

**Step 3: Verify tests pass**

Run:
```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet test src/GxMcp.Worker.Tests/GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~SourceSearch"
```
Expected: PASS.

---

### Task 6: Documentation and Environment Levers

**Files:**
- Modify: `docs/environment_variables.md`
- Modify: `CHANGELOG.md`

**Step 1: Update `docs/environment_variables.md`**

Add `GXMCP_READ_CACHE_TTL_SEC` and document `GXMCP_PROFILE` in the environment variables table.

**Step 2: Update `CHANGELOG.md` under `## Unreleased` -> `### Changed`**

Document the performance improvements:
- Zero-AST streaming validation in worker `IsCacheableSuccessEnvelope`
- Zero-copy parameter merge in `CommandDispatcher.DispatchInternal`
- Tool profile result caching in `ToolProfileFilter`
- Extended `ObjectService` read cache TTL (300s default) with `GXMCP_READ_CACHE_TTL_SEC`
- Reduced allocation overhead in `SourceSearchService` candidate loops

---

### Task 7: Full Solution Build & Verification

**Step 1: Build the entire solution**

Run:
```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet build Genexus18MCP.sln -v:minimal
```
Expected: 0 Errors.

**Step 2: Run all Gateway and Worker tests**

Run:
```powershell
$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet test Genexus18MCP.sln
```
Expected: 100% tests pass.
