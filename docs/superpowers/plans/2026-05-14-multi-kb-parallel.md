# Multi-KB Parallel Support — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable N concurrent KBs (1 Worker process per KB) coordinated by a `WorkerPool` in the Gateway. Every tool gains an optional `kb` parameter; chamadas a KBs diferentes não bloqueiam entre si.

**Architecture:** Gateway mantém `WorkerPool` (max 3 KBs, idle LRU eviction). `KbResolver` mapeia `kb` arg → `KbHandle`. Worker fica praticamente intocado (apenas `KbService` deixa de ser static). Backward-compat preservada via migração de `Environment.KBPath` legado.

**Tech Stack:** .NET 8, C#, ASP.NET Core Kestrel (Gateway), WinForms STA (Worker), JSON-RPC stdio IPC, xUnit + Moq (tests).

**Spec:** `docs/superpowers/specs/2026-05-14-multi-kb-parallel-design.md`

---

## File Structure

**Gateway — new files:**
- `src/GxMcp.Gateway/KbHandle.cs` — record (Alias, Path)
- `src/GxMcp.Gateway/KbResolver.cs` — resolução `kb` arg → handle
- `src/GxMcp.Gateway/WorkerPool.cs` — pool keyed by alias

**Gateway — modified files:**
- `src/GxMcp.Gateway/Configuration.cs` — schema `KBs[]`, `DefaultKb`, `MaxOpenKbs`, legacy migration
- `src/GxMcp.Gateway/WorkerProcess.cs` — construtor recebe `KbHandle`
- `src/GxMcp.Gateway/Program.cs` — `_worker` → `_workerPool`, `SendWorkerCommandAsync(KbHandle, ...)`, `RestartWorker(KbHandle)`
- `src/GxMcp.Gateway/McpRouter.cs` — propagação do `kb` arg até `SendWorkerCommandAsync`
- `src/GxMcp.Gateway/Routers/*.cs` — cada router lê `kb` de args
- `src/GxMcp.Gateway/tool_definitions.json` — adiciona `kb` em cada tool; nova tool `genexus_kb`

**Worker — modified files:**
- `src/GxMcp.Worker/Services/KbService.cs` — `_kb`, `_kbLock`, `_isOpenInProgress` deixam de ser static

**Tests — new files:**
- `src/GxMcp.Gateway.Tests/KbResolverTests.cs`
- `src/GxMcp.Gateway.Tests/WorkerPoolTests.cs`
- `src/GxMcp.Gateway.Tests/ConfigurationLegacyMigrationTests.cs`

---

## Task 1: Add `KbHandle` record

**Files:**
- Create: `src/GxMcp.Gateway/KbHandle.cs`

- [ ] **Step 1: Create KbHandle**

```csharp
namespace GxMcp.Gateway
{
    public sealed record KbHandle(string Alias, string Path)
    {
        public string NormalizedAlias => Alias.Trim().ToLowerInvariant();
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/GxMcp.Gateway/GxMcp.Gateway.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/GxMcp.Gateway/KbHandle.cs
git commit -m "feat(gateway): add KbHandle record for multi-KB routing"
```

---

## Task 2: Extend Configuration schema (KBs array + DefaultKb + MaxOpenKbs)

**Files:**
- Modify: `src/GxMcp.Gateway/Configuration.cs`

- [ ] **Step 1: Add KbEntry class + new fields to EnvironmentConfig and ServerConfig**

In `Configuration.cs`, add inside `EnvironmentConfig`:

```csharp
public class EnvironmentConfig
{
    public string? KBPath { get; set; }                  // LEGACY
    public string? GX_SHADOW_PATH { get; set; }
    public string? DefaultKb { get; set; }
    public List<KbEntry> KBs { get; set; } = new List<KbEntry>();
}

public class KbEntry
{
    public string Alias { get; set; } = "";
    public string Path { get; set; } = "";
}
```

In `ServerConfig`, add:

```csharp
public int MaxOpenKbs { get; set; } = 3;
```

- [ ] **Step 2: Legacy migration in `ParseConfig`**

After deserialization, before returning `config`:

```csharp
// Legacy migration: KBPath without KBs[] -> synthesize entry
if ((config.Environment?.KBs == null || config.Environment.KBs.Count == 0) &&
    !string.IsNullOrWhiteSpace(config.Environment?.KBPath))
{
    var path = config.Environment.KBPath;
    var alias = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')).ToLowerInvariant();
    if (string.IsNullOrEmpty(alias)) alias = "default";
    config.Environment.KBs = new List<KbEntry> { new KbEntry { Alias = alias, Path = path } };
    if (string.IsNullOrWhiteSpace(config.Environment.DefaultKb))
    {
        config.Environment.DefaultKb = alias;
    }
    Program.Log($"[Gateway] Legacy KBPath migrated to KBs[{alias}] / DefaultKb={alias}");
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/GxMcp.Gateway/GxMcp.Gateway.csproj`
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/GxMcp.Gateway/Configuration.cs
git commit -m "feat(gateway): config schema KBs[]/DefaultKb/MaxOpenKbs + legacy migration"
```

---

## Task 3: Write `KbResolver` (TDD)

**Files:**
- Create: `src/GxMcp.Gateway/KbResolver.cs`
- Test: `src/GxMcp.Gateway.Tests/KbResolverTests.cs`

- [ ] **Step 1: Write failing tests**

Create `src/GxMcp.Gateway.Tests/KbResolverTests.cs`:

```csharp
using System.Collections.Generic;
using FluentAssertions;
using GxMcp.Gateway;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class KbResolverTests
    {
        private static Configuration MakeConfig(params (string alias, string path)[] kbs)
        {
            var env = new EnvironmentConfig();
            foreach (var (alias, path) in kbs)
                env.KBs.Add(new KbEntry { Alias = alias, Path = path });
            if (kbs.Length > 0) env.DefaultKb = kbs[0].alias;
            return new Configuration { Environment = env };
        }

        [Fact]
        public void Resolves_alias_from_config()
        {
            var cfg = MakeConfig(("customer", "C:/KB/Customer"), ("order", "C:/KB/Order"));
            var resolver = new KbResolver(cfg);
            var handle = resolver.Resolve("order", openKbs: new List<KbHandle>());
            handle.Alias.Should().Be("order");
            handle.Path.Should().Be("C:/KB/Order");
        }

        [Fact]
        public void Falls_back_to_default_when_arg_null_and_no_open_kbs()
        {
            var cfg = MakeConfig(("customer", "C:/KB/Customer"));
            var resolver = new KbResolver(cfg);
            var handle = resolver.Resolve(null, openKbs: new List<KbHandle>());
            handle.Alias.Should().Be("customer");
        }

        [Fact]
        public void Uses_sole_open_kb_when_arg_null_and_one_open()
        {
            var cfg = MakeConfig(("customer", "C:/KB/Customer"), ("order", "C:/KB/Order"));
            var resolver = new KbResolver(cfg);
            var open = new List<KbHandle> { new KbHandle("order", "C:/KB/Order") };
            var handle = resolver.Resolve(null, openKbs: open);
            handle.Alias.Should().Be("order");
        }

        [Fact]
        public void Throws_ambiguous_when_arg_null_and_multiple_open()
        {
            var cfg = MakeConfig(("customer", "C:/KB/Customer"), ("order", "C:/KB/Order"));
            var resolver = new KbResolver(cfg);
            var open = new List<KbHandle>
            {
                new KbHandle("customer", "C:/KB/Customer"),
                new KbHandle("order", "C:/KB/Order"),
            };
            var act = () => resolver.Resolve(null, openKbs: open);
            act.Should().Throw<KbResolutionException>().Where(e => e.Code == "KB_AMBIGUOUS");
        }

        [Fact]
        public void Throws_not_found_for_unknown_alias()
        {
            var cfg = MakeConfig(("customer", "C:/KB/Customer"));
            var resolver = new KbResolver(cfg);
            var act = () => resolver.Resolve("nope", openKbs: new List<KbHandle>());
            act.Should().Throw<KbResolutionException>().Where(e => e.Code == "KB_NOT_FOUND");
        }

        [Fact]
        public void Returns_adhoc_handle_for_existing_absolute_path()
        {
            // path must exist on disk for adhoc registration
            var tmp = System.IO.Directory.CreateTempSubdirectory();
            try
            {
                var cfg = MakeConfig(("customer", "C:/KB/Customer"));
                var resolver = new KbResolver(cfg);
                var handle = resolver.Resolve(tmp.FullName, openKbs: new List<KbHandle>());
                handle.Path.Should().Be(tmp.FullName);
                handle.Alias.Should().Be(tmp.Name.ToLowerInvariant());
            }
            finally { tmp.Delete(true); }
        }
    }
}
```

- [ ] **Step 2: Run tests — expect compile failure (KbResolver missing)**

Run: `dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter KbResolver`
Expected: compile error — `KbResolver` not defined.

- [ ] **Step 3: Implement KbResolver**

Create `src/GxMcp.Gateway/KbResolver.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GxMcp.Gateway
{
    public sealed class KbResolutionException : Exception
    {
        public string Code { get; }
        public KbResolutionException(string code, string message) : base(message) { Code = code; }
    }

    public sealed class KbResolver
    {
        private readonly Configuration _config;

        public KbResolver(Configuration config) { _config = config; }

        public KbHandle Resolve(string? kbArg, IReadOnlyCollection<KbHandle> openKbs)
        {
            // 1. No arg
            if (string.IsNullOrWhiteSpace(kbArg))
            {
                if (openKbs.Count == 1) return openKbs.First();
                if (openKbs.Count == 0)
                {
                    var def = _config.Environment?.DefaultKb;
                    if (!string.IsNullOrWhiteSpace(def))
                    {
                        var entry = _config.Environment!.KBs.FirstOrDefault(
                            k => string.Equals(k.Alias, def, StringComparison.OrdinalIgnoreCase));
                        if (entry == null)
                            throw new KbResolutionException("KB_NOT_FOUND",
                                $"DefaultKb '{def}' not declared in Environment.KBs[]");
                        return new KbHandle(entry.Alias, entry.Path);
                    }
                    throw new KbResolutionException("KB_AMBIGUOUS",
                        "No 'kb' parameter and no DefaultKb configured.");
                }
                throw new KbResolutionException("KB_AMBIGUOUS",
                    $"Multiple KBs open ({string.Join(",", openKbs.Select(k => k.Alias))}); 'kb' parameter required.");
            }

            // 2. Match declared alias (case-insensitive)
            var declared = _config.Environment?.KBs?.FirstOrDefault(
                k => string.Equals(k.Alias, kbArg, StringComparison.OrdinalIgnoreCase));
            if (declared != null) return new KbHandle(declared.Alias, declared.Path);

            // 3. Match already-open alias (registered ad-hoc earlier)
            var openMatch = openKbs.FirstOrDefault(
                k => string.Equals(k.Alias, kbArg, StringComparison.OrdinalIgnoreCase));
            if (openMatch != null) return openMatch;

            // 4. Absolute path fallback
            if (Path.IsPathRooted(kbArg) && Directory.Exists(kbArg))
            {
                var alias = Path.GetFileName(kbArg.TrimEnd('\\', '/')).ToLowerInvariant();
                if (string.IsNullOrEmpty(alias)) alias = "adhoc";
                return new KbHandle(alias, kbArg);
            }

            throw new KbResolutionException("KB_NOT_FOUND",
                $"Unknown KB '{kbArg}'. Declare an alias in config.Environment.KBs[] or pass an absolute path to an existing directory.");
        }
    }
}
```

- [ ] **Step 4: Run tests — expect PASS**

Run: `dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter KbResolver`
Expected: all 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/GxMcp.Gateway/KbResolver.cs src/GxMcp.Gateway.Tests/KbResolverTests.cs
git commit -m "feat(gateway): KbResolver with alias/path/default-fallback rules"
```

---

## Task 4: De-statify `KbService` in Worker

**Files:**
- Modify: `src/GxMcp.Worker/Services/KbService.cs`

- [ ] **Step 1: Convert statics to instance fields**

Remove `static` from `_kb`, `_isOpenInProgress`, `_kbLock` (keep progress counters static since they're per-process anyway and there's still 1 KB per worker):

```csharp
private dynamic _kb;
private bool _isOpenInProgress = false;
private readonly object _kbLock = new object();
```

(The progress counters `_processedCount`, `_totalCount`, `_isIndexing`, `_currentStatus` can stay as-is — they're scoped to one KB anyway and there's one KbService per Worker.)

- [ ] **Step 2: Build worker**

Run: `dotnet build src/GxMcp.Worker/GxMcp.Worker.csproj`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/GxMcp.Worker/Services/KbService.cs
git commit -m "refactor(worker): KbService instance-based (multi-KB readiness)"
```

---

## Task 5: Refactor `WorkerProcess` to take a `KbHandle`

**Files:**
- Modify: `src/GxMcp.Gateway/WorkerProcess.cs`

- [ ] **Step 1: Add KbHandle field, modify constructor**

Add field near top of class:
```csharp
public KbHandle Kb { get; }
```

Change constructor signature and body:
```csharp
public WorkerProcess(Configuration config, KbHandle kb)
{
    _config = config;
    Kb = kb;
    _workerIdleTimeout = TimeSpan.FromMinutes(Math.Max(1, _config.Server?.WorkerIdleTimeoutMinutes ?? 5));
    _writerTask = Task.Run(ProcessQueueAsync);
}
```

- [ ] **Step 2: Use Kb.Path in Start() instead of _config.Environment.KBPath**

Find in `Start()`:
```csharp
string kbPath = _config.Environment?.KBPath ?? string.Empty;
```
Replace with:
```csharp
string kbPath = Kb.Path;
```

The rest stays the same (env vars `GX_KB_PATH`, `--kb` arg, `GX_SHADOW_PATH`).

- [ ] **Step 3: Build (will FAIL — Program.cs still calls old ctor)**

Run: `dotnet build src/GxMcp.Gateway/GxMcp.Gateway.csproj`
Expected: error CS7036 in Program.cs `_worker = new WorkerProcess(config);` — missing kb arg. Acceptable: fixed in Task 7.

- [ ] **Step 4: Commit (deliberate work-in-progress, fixed by Task 7)**

```bash
git add src/GxMcp.Gateway/WorkerProcess.cs
git commit -m "refactor(gateway): WorkerProcess takes KbHandle (WIP, Program.cs updated next)"
```

---

## Task 6: Implement `WorkerPool` (TDD light — instance behaviors only)

**Files:**
- Create: `src/GxMcp.Gateway/WorkerPool.cs`
- Test: `src/GxMcp.Gateway.Tests/WorkerPoolTests.cs`

Note: WorkerPool spawns real processes which is hard to unit test cleanly. Tests cover non-spawn logic (capacity, eviction selection); end-to-end spawn covered manually.

- [ ] **Step 1: Write failing tests (capacity + eviction selection only)**

Create `src/GxMcp.Gateway.Tests/WorkerPoolTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using FluentAssertions;
using GxMcp.Gateway;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class WorkerPoolTests
    {
        [Fact]
        public void ListOpen_returns_added_handles()
        {
            var pool = new WorkerPool(new Configuration
            {
                Server = new ServerConfig { MaxOpenKbs = 2 }
            });
            pool.RegisterForTest(new KbHandle("a", "C:/A"));
            pool.RegisterForTest(new KbHandle("b", "C:/B"));
            var open = pool.ListOpen();
            open.Should().HaveCount(2);
            open.Should().Contain(h => h.Alias == "a");
            open.Should().Contain(h => h.Alias == "b");
        }

        [Fact]
        public void SelectVictim_picks_oldest_idle()
        {
            var pool = new WorkerPool(new Configuration
            {
                Server = new ServerConfig { MaxOpenKbs = 2 }
            });
            pool.RegisterForTest(new KbHandle("a", "C:/A"), lastActivity: DateTime.UtcNow.AddMinutes(-10));
            pool.RegisterForTest(new KbHandle("b", "C:/B"), lastActivity: DateTime.UtcNow.AddMinutes(-1));
            var victim = pool.SelectVictimForTest();
            victim.Should().NotBeNull();
            victim!.Alias.Should().Be("a");
        }

        [Fact]
        public void IsAtCapacity_respects_MaxOpenKbs()
        {
            var pool = new WorkerPool(new Configuration
            {
                Server = new ServerConfig { MaxOpenKbs = 2 }
            });
            pool.IsAtCapacity().Should().BeFalse();
            pool.RegisterForTest(new KbHandle("a", "C:/A"));
            pool.RegisterForTest(new KbHandle("b", "C:/B"));
            pool.IsAtCapacity().Should().BeTrue();
        }
    }
}
```

- [ ] **Step 2: Implement WorkerPool**

Create `src/GxMcp.Gateway/WorkerPool.cs`:

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GxMcp.Gateway
{
    public sealed class WorkerPoolFullException : Exception
    {
        public IReadOnlyList<KbHandle> OpenKbs { get; }
        public WorkerPoolFullException(IReadOnlyList<KbHandle> openKbs)
            : base($"WorkerPool full ({openKbs.Count} KBs open). Close one with genexus_kb action=close before opening another.")
        { OpenKbs = openKbs; }
    }

    public sealed class WorkerPool
    {
        private readonly Configuration _config;
        private readonly ConcurrentDictionary<string, Entry> _entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly object _spawnLock = new object();

        public event Action<string>? OnRpcResponse;
        public event Action<KbHandle>? OnWorkerExited;

        public WorkerPool(Configuration config) { _config = config; }

        private sealed class Entry
        {
            public KbHandle Handle = null!;
            public WorkerProcess? Worker;
            public DateTime LastActivityUtc = DateTime.UtcNow;
        }

        public IReadOnlyList<KbHandle> ListOpen() =>
            _entries.Values.Select(e => e.Handle).ToArray();

        public bool IsAtCapacity()
        {
            int max = _config.Server?.MaxOpenKbs ?? 3;
            return _entries.Count >= max;
        }

        public async Task<WorkerProcess> AcquireAsync(KbHandle handle, CancellationToken ct)
        {
            var entry = _entries.GetOrAdd(handle.NormalizedAlias, _ => new Entry { Handle = handle });
            if (entry.Worker != null && !ct.IsCancellationRequested)
            {
                entry.LastActivityUtc = DateTime.UtcNow;
                return entry.Worker;
            }

            lock (_spawnLock)
            {
                if (entry.Worker != null) return entry.Worker;

                // Capacity check
                int max = _config.Server?.MaxOpenKbs ?? 3;
                if (_entries.Count > max)
                {
                    var victim = SelectVictim();
                    if (victim == null || _entries.Count > max)
                    {
                        _entries.TryRemove(handle.NormalizedAlias, out _);
                        throw new WorkerPoolFullException(ListOpen());
                    }
                    EvictEntry(victim);
                }

                var worker = new WorkerProcess(_config, handle);
                worker.OnRpcResponse += json => OnRpcResponse?.Invoke(json);
                worker.OnWorkerExited += () =>
                {
                    OnWorkerExited?.Invoke(handle);
                    _entries.TryRemove(handle.NormalizedAlias, out _);
                };
                worker.Start();
                entry.Worker = worker;
                entry.LastActivityUtc = DateTime.UtcNow;
                return worker;
            }
        }

        public bool Close(string alias)
        {
            if (_entries.TryRemove(alias.ToLowerInvariant(), out var entry))
            {
                try { entry.Worker?.Stop(); } catch { }
                return true;
            }
            return false;
        }

        public void StopAll()
        {
            foreach (var e in _entries.Values)
            {
                try { e.Worker?.Stop(); } catch { }
            }
            _entries.Clear();
        }

        private Entry? SelectVictim()
        {
            // Pick the entry with the oldest LastActivityUtc; skip _entries currently being spawned.
            return _entries.Values
                .Where(e => e.Worker != null)
                .OrderBy(e => e.LastActivityUtc)
                .FirstOrDefault();
        }

        private void EvictEntry(Entry entry)
        {
            try { entry.Worker?.Stop(); } catch { }
            _entries.TryRemove(entry.Handle.NormalizedAlias, out _);
        }

        // -- test hooks --
        internal void RegisterForTest(KbHandle h, DateTime? lastActivity = null)
        {
            _entries[h.NormalizedAlias] = new Entry
            {
                Handle = h,
                LastActivityUtc = lastActivity ?? DateTime.UtcNow
            };
        }

        internal KbHandle? SelectVictimForTest()
        {
            return _entries.Values.OrderBy(e => e.LastActivityUtc).FirstOrDefault()?.Handle;
        }
    }
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test src/GxMcp.Gateway.Tests/GxMcp.Gateway.Tests.csproj --filter WorkerPool`
Expected: 3 tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/GxMcp.Gateway/WorkerPool.cs src/GxMcp.Gateway.Tests/WorkerPoolTests.cs
git commit -m "feat(gateway): WorkerPool with LRU eviction and capacity guard"
```

---

## Task 7: Wire `WorkerPool` into `Program.cs`

**Files:**
- Modify: `src/GxMcp.Gateway/Program.cs`

- [ ] **Step 1: Replace `_worker` field**

Find:
```csharp
private static WorkerProcess? _worker;
```
Replace with:
```csharp
private static WorkerPool? _workerPool;
private static KbResolver? _kbResolver;
```

- [ ] **Step 2: Replace `StartWorker` with `InitWorkerPool`**

Replace the `StartWorker` method body:
```csharp
private static void InitWorkerPool(Configuration config)
{
    _workerPool = new WorkerPool(config);
    _kbResolver = new KbResolver(config);
    _workerPool.OnRpcResponse += HandleWorkerResponse;
    _workerPool.OnWorkerExited += (kb) =>
    {
        Log($"Worker for KB '{kb.Alias}' exited. Aborting its pending requests...");
        // (request->worker mapping not tracked; aborting by alias substring would be unsound)
        // Best-effort: leave pending requests to be reaped by stale-cleanup timer.
    };
}
```

- [ ] **Step 3: Replace caller `StartWorker(config)` with `InitWorkerPool(config)`**

Find every call to `StartWorker(` (likely in Main / OnConfigurationChanged) and rename. Also fix `RestartWorker` to accept optional alias (restart all if null):

```csharp
private static void RestartWorker(Configuration config, string? alias = null)
{
    if (_workerPool != null)
    {
        if (alias == null) _workerPool.StopAll();
        else _workerPool.Close(alias);
    }
    _semanticCache.Clear();
    InitWorkerPool(config);
    BroadcastToolsListChanged("worker_restarted");
    BroadcastResourcesListChanged("worker_restarted");
}
```

- [ ] **Step 4: Update `SendWorkerCommandAsync` signature to take `KbHandle`**

Add `KbHandle kb` as the first parameter:
```csharp
private static async Task<JObject?> SendWorkerCommandAsync(
    KbHandle kb,
    JObject workerCommand,
    int timeoutMs,
    string timeoutLogMessage,
    Func<JObject, JObject> onSuccess,
    Func<string?, string, JObject> onTimeout,
    string toolName = "unknown",
    JObject? toolArgs = null,
    bool trackOperation = false)
{
    // ...existing body, but replace:
    //   await _worker!.SendCommandAsync(workerRequest.ToString(Formatting.None));
    // with:
    var worker = await _workerPool!.AcquireAsync(kb, CancellationToken.None);
    await worker.SendCommandAsync(workerRequest.ToString(Formatting.None));
    // ...rest unchanged
}
```

- [ ] **Step 5: Resolve KB at top of `ProcessMcpRequest` for `tools/call`**

In `ProcessMcpRequest`, after extracting `earlyToolName` and before invoking routers, for `tools/call` method extract `kb` from arguments and resolve:

```csharp
KbHandle? resolvedKb = null;
if (string.Equals(method, "tools/call", StringComparison.OrdinalIgnoreCase))
{
    var paramsObj = request["params"] as JObject;
    var argsObj = paramsObj?["arguments"] as JObject;
    string? kbArg = argsObj?["kb"]?.ToString();
    try
    {
        resolvedKb = _kbResolver!.Resolve(kbArg, _workerPool!.ListOpen());
    }
    catch (KbResolutionException ex)
    {
        return new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = idToken,
            ["error"] = new JObject
            {
                ["code"] = -32602,
                ["message"] = ex.Message,
                ["data"] = new JObject { ["code"] = ex.Code, ["openKbs"] = JArray.FromObject(_workerPool!.ListOpen().Select(k => k.Alias)) }
            }
        };
    }
    // Strip `kb` from worker-bound args (worker doesn't need it)
    argsObj?.Remove("kb");
}
```

Then pass `resolvedKb` down through router dispatch. **Practical approach:** stash on a `ThreadLocal<KbHandle>` or attach to the `JObject` request:

```csharp
if (resolvedKb != null)
    request["_resolvedKb"] = JObject.FromObject(new { alias = resolvedKb.Alias, path = resolvedKb.Path });
```

Then in `SendWorkerCommandAsync` callers (in the routers), read `_resolvedKb` from the outer request and pass into `SendWorkerCommandAsync(kb, ...)`.

- [ ] **Step 6: Update all 8 call sites of `SendWorkerCommandAsync`**

For each call site, fetch the resolved KB from the request context and pass as first arg. Since routers receive the request, propagate via a context object or parameter:

```csharp
// Existing helper threading: change SendWorkerCommandAsync to accept kb;
// then in each router method that builds a worker command, add `KbHandle kb` to the method signature.
```

Use Grep to find all call sites:
```
grep -n "SendWorkerCommandAsync(" src/GxMcp.Gateway/**/*.cs
```

For each, prepend `kb` (extract from the request context object that flows through routers).

- [ ] **Step 7: Build**

Run: `dotnet build src/GxMcp.Gateway/GxMcp.Gateway.csproj`
Expected: succeeds.

- [ ] **Step 8: Commit**

```bash
git add src/GxMcp.Gateway/Program.cs src/GxMcp.Gateway/Routers/*.cs
git commit -m "feat(gateway): route tool calls through WorkerPool by resolved KB"
```

---

## Task 8: Add `kb` parameter to every tool definition

**Files:**
- Modify: `src/GxMcp.Gateway/tool_definitions.json`

- [ ] **Step 1: For each tool's `inputSchema.properties`, add `kb` field**

For each tool object in `tool_definitions.json` (except `genexus_whoami`, `genexus_logs`, `genexus_doc`), add:

```json
"kb": {
  "type": "string",
  "description": "Optional KB alias or absolute path. If omitted and only one KB is open, uses it; if multiple KBs are open, an explicit value is required."
}
```

- [ ] **Step 2: Add new `genexus_kb` tool**

Append:

```json
{
  "name": "genexus_kb",
  "description": "Manage open Knowledge Bases (list, open, close, set_default).",
  "inputSchema": {
    "type": "object",
    "properties": {
      "action": { "type": "string", "enum": ["list", "open", "close", "set_default"] },
      "alias":  { "type": "string", "description": "KB alias (for open/close/set_default)." },
      "path":   { "type": "string", "description": "KB path (required for action=open if alias not declared)." }
    },
    "required": ["action"]
  }
}
```

- [ ] **Step 3: Build (json copied to bin via csproj content)**

Run: `dotnet build src/GxMcp.Gateway/GxMcp.Gateway.csproj`
Expected: succeeds, copies tool_definitions.json.

- [ ] **Step 4: Commit**

```bash
git add src/GxMcp.Gateway/tool_definitions.json
git commit -m "feat(tools): add optional kb param to all tools + new genexus_kb tool"
```

---

## Task 9: Implement `genexus_kb` tool handler

**Files:**
- Modify: `src/GxMcp.Gateway/Routers/SystemRouter.cs` (or wherever meta tools live)

- [ ] **Step 1: Add handler that calls WorkerPool directly (no worker dispatch)**

```csharp
case "genexus_kb":
{
    string action = args?["action"]?.ToString() ?? "list";
    var pool = Program.GetWorkerPool();   // expose static accessor
    switch (action.ToLowerInvariant())
    {
        case "list":
            return new JObject
            {
                ["kbs"] = JArray.FromObject(pool.ListOpen().Select(k => new { k.Alias, k.Path }))
            };
        case "open":
        {
            string? alias = args?["alias"]?.ToString();
            string? path  = args?["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(path))
                return Error("Missing 'path' for action=open");
            var handle = new KbHandle(
                string.IsNullOrWhiteSpace(alias)
                    ? System.IO.Path.GetFileName(path.TrimEnd('\\','/')).ToLowerInvariant()
                    : alias,
                path);
            await pool.AcquireAsync(handle, CancellationToken.None);
            return new JObject { ["opened"] = handle.Alias };
        }
        case "close":
        {
            // Historical snippet: since #683/#689 `close` (like `select`, `set_session_default`,
            // `set_persistent_default` and `set_default`) takes `kb` as an alias for `alias`, and the
            // shipped message is "Missing 'alias' (or 'kb') for action=close." (Program.GatewayTools.cs).
            string? alias = args?["alias"]?.ToString() ?? args?["kb"]?.ToString();
            if (string.IsNullOrWhiteSpace(alias)) return Error("Missing 'alias' (or 'kb') for action=close");
            return new JObject { ["closed"] = pool.Close(alias) };
        }
        case "set_default":
            // runtime-only; not persisted
            return Error("set_default not implemented in v1 (edit config.json DefaultKb).");
        default:
            return Error($"Unknown action '{action}'");
    }
}
```

- [ ] **Step 2: Build + commit**

Run: `dotnet build src/GxMcp.Gateway/GxMcp.Gateway.csproj`

```bash
git add src/GxMcp.Gateway/Routers/SystemRouter.cs src/GxMcp.Gateway/Program.cs
git commit -m "feat(tools): implement genexus_kb meta-tool"
```

---

## Task 10: End-to-end smoke (manual, documented)

- [ ] **Step 1: Build full solution**

Run: `dotnet build Genexus18MCP.sln`
Expected: succeeds.

- [ ] **Step 2: Run existing test suite**

Run: `dotnet test`
Expected: all pre-existing tests still pass.

- [ ] **Step 3: Manual smoke with 2 KBs**

```pwsh
# Edit config.json to have:
#   Environment.KBs = [
#     { "alias": "kb1", "path": "C:/Projetos/SomeRealKB" },
#     { "alias": "kb2", "path": "C:/Projetos/AnotherKB" }
#   ]
#   Environment.DefaultKb = "kb1"

# Start gateway in stdio
.\src\GxMcp.Gateway\bin\Debug\net8.0\GxMcp.Gateway.exe

# In another shell, send tool/call with kb=kb1 and kb=kb2 concurrently
# Verify: both return; wallclock < sum(individual times)
```

- [ ] **Step 4: Commit smoke notes**

Create `docs/superpowers/specs/2026-05-14-multi-kb-smoke-notes.md` with timings.

```bash
git add docs/superpowers/specs/2026-05-14-multi-kb-smoke-notes.md
git commit -m "docs(smoke): multi-KB parallel verification timings"
```

---

## Task 11: Bump version + CHANGELOG

**Files:**
- Modify: `package.json`, `CHANGELOG.md`, `src/GxMcp.Gateway/GxMcp.Gateway.csproj`

- [ ] **Step 1: Bump to 2.3.0**

In `package.json`: `"version": "2.3.0"`
In `CHANGELOG.md`: add entry for `[2.3.0]` documenting multi-KB support.
In `GxMcp.Gateway.csproj`: bump `<InformationalVersion>` if hardcoded.

- [ ] **Step 2: Commit**

```bash
git add package.json CHANGELOG.md src/GxMcp.Gateway/GxMcp.Gateway.csproj
git commit -m "chore(release): bump to 2.3.0 for multi-KB parallel support"
```

---

## Self-review notes

- All decisions from the spec map to tasks (pool, resolver, config migration, tool schema, genexus_kb tool, idempotency cache already kb-scoped, version bump).
- IdempotencyCache already takes `kbPath` per call — no changes needed there. Confirmed in code: `IdempotencyCache.TryGet(string kbPath, ...)`. Just need to pass `resolvedKb.Path` from the dispatcher.
- Task 7 step 6 says "8 call sites" — actual count per `grep`: 8 in Gateway. Confirmed.
- The phrase "all pre-existing tests still pass" in Task 10 is appropriate because the refactor is additive and configs without `KBs[]` migrate transparently.
- Open risk flagged in Task 7 step 5: per-pending-request → worker-alias mapping is NOT added. If a Worker crashes, pending requests bound to it will only be released by the stale-pending sweep (1-65min). Acceptable for v1; track as follow-up.

---

## Follow-ups (post-v2.3.0)

1. Map `_pendingRequests` entries to their worker alias so a per-worker crash can abort just those.
2. Add `genexus_kb action=set_default` that persists to config.json.
3. Surface per-KB memory in `genexus_whoami` so the LLM can self-throttle.
