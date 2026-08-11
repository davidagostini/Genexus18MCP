# Plan 072: Harden `ObjectMover`'s fallback type resolution (prefer exact FQNs, constrain the simple-name scan)

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat e756dd2..HEAD -- src/GxMcp.Worker/Helpers/ObjectMover.cs src/GxMcp.Worker/Services/ObjectService.cs src/GxMcp.Worker.Tests/ObjectMoverTests.cs`
> If any changed since this plan was written, compare the "Current state"
> excerpts against live code before proceeding; on a mismatch, STOP.

## Status

- **Priority**: P3
- **Effort**: S
- **Risk**: LOW–MED
- **Depends on**: none
- **Category**: robustness (reflection fallback)
- **Planned at**: commit `e756dd2`, 2026-08-10

## Why this matters

`ObjectMover.SetParentAndSave` (v2.35.0 folder/module placement, issue #50) resolves
the SDK `EntityManager` by a chain of reflection lookups. The first two are exact
FQNs — safe. But the final fallback, `FindFirstType("EntityManager")`, scans **every
loaded assembly** and returns the first type whose simple name is exactly
"EntityManager", with no namespace constraint. If any assembly in the worker's
AppDomain happens to define a type named `EntityManager` that is NOT the
`Artech.Udm.Framework.EntityManager` the move needs, the fallback binds the wrong
type, and `FindMethod` then picks the first `SaveWithParent`/`UpdateParent` overload
that merely has a compatible first parameter — invoking an arbitrary save on a
possibly-wrong manager. The impact is bounded: `MoveObject` verifies the parent
afterwards and reports `MoveNotPersisted` rather than a false success
(`ObjectService.cs:1546-1572`), so the worst realistic outcome is a confusing
`MoveFailed` on an SDK build where the first two FQNs fail to resolve. But the
wrong-type risk is real, silent, and environment-dependent — the fix is cheap:
never bind by simple name outside the `Artech.*` namespace family, and log which
type/strategy was actually used so a wrong binding is diagnosable.

## Current state

`src/GxMcp.Worker/Helpers/ObjectMover.cs:44-90` (excerpts):

```csharp
// 2. Resolve the Udm EntityManager (same lookup WebFormSaveDiagnostics uses).
Type emType = FindType("Artech.Layers.BL.EntityManager")
            ?? FindType("Artech.Udm.Framework.EntityManager")
            ?? FindFirstType("EntityManager");        // <-- scans ALL assemblies, any namespace
if (emType == null)
    return new MoveResult { Ok = false, Error = "EntityManager type not found in loaded assemblies" };

object prefs = BuildSavePreferences();
var swp = FindMethod(emType, "SaveWithParent", obj, container);   // first name+arg0-compatible overload
...
```

And the helpers (lines 214-254):

```csharp
private static Type FindFirstType(string simpleName)
{
    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
    {
        Type[] types;
        try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
        if (types == null) continue;
        foreach (var t in types)
            if (t != null && t.Name == simpleName) return t;      // <-- no namespace filter
    }
    return null;
}
```

`FindMethod` (lines 60-81) also accepts the *first* overload whose first parameter is
type-compatible (`ps[0].ParameterType.IsInstanceOfType(arg0)`), not the most specific
signature.

### Convention

C# (.NET Framework 4.8, x86), match the surrounding file style. Tests: xUnit,
reflection into `internal`/`private` statics is the established pattern. `ObjectMover`
is `internal static`; the new selection helper should be pure and testable without
the SDK.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build worker | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Run one test file | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~ObjectMoverTests"` | all pass |
| Full worker tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` | ~1,790 pass (known flaky pair as in plan 068) |

## Scope

**In scope**:
- `src/GxMcp.Worker/Helpers/ObjectMover.cs` — constrain the simple-name fallback to
  `Artech.*` namespaces (exact FQNs first, then Artech-namespaced simple-name match,
  then give up); log the resolved type and strategy; prefer the most specific
  `FindMethod` overload by parameter count before falling back to type-compatible.
- `src/GxMcp.Worker.Tests/ObjectMoverTests.cs` — new test file for the pure selection
  logic (create it).

**Out of scope**:
- The move semantics, `MoveResult` shape, or `ObjectService.MoveObject` — unchanged.
- Making `ObjectMover` fully unit-testable (it needs the SDK at runtime) — only the
  *selection decision* is extracted for testing.
- `KBObjectSavePreferences` / `BuildSavePreferences` — unchanged.

## Git workflow

- Branch: `advisor/072-objectmover-type-selection-hardening`
- Commit style: `fix(worker): constrain ObjectMover EntityManager fallback to Artech.* namespaces`
- Do NOT push or open a PR unless instructed.

## Steps

### Step 1: Replace `FindFirstType` with a namespace-constrained selection

Replace the `FindFirstType("EntityManager")` fallback with a helper that only binds
Artech-namespaced types:

```csharp
// Resolve the Udm EntityManager. Exact FQNs first; the simple-name fallback is
// constrained to Artech.* namespaces so an unrelated "EntityManager" type in any
// other loaded assembly can never be bound by accident (the worker hosts the whole
// SDK plus its own code, so a bare simple-name scan is unsafe).
private static Type FindArtechEntityManager()
{
    var preferred = new[]
    {
        "Artech.Layers.BL.EntityManager",
        "Artech.Udm.Framework.EntityManager"
    };
    foreach (var fqn in preferred)
    {
        var t = FindType(fqn);
        if (t != null) return t;
    }
    return FindTypeBySimpleNameInNamespace("EntityManager", ns => ns == "Artech"
        || ns.StartsWith("Artech.", StringComparison.Ordinal));
}

private static Type FindTypeBySimpleNameInNamespace(string simpleName, Func<string, bool> namespaceFilter)
{
    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
    {
        Type[] types;
        try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
        if (types == null) continue;
        foreach (var t in types)
        {
            if (t == null || t.Name != simpleName) continue;
            string ns = t.Namespace ?? string.Empty;
            if (namespaceFilter(ns)) return t;
        }
    }
    return null;
}
```

Update the call site at `ObjectMover.cs:44-46` to use `FindArtechEntityManager()`, and
log the resolved type so a wrong binding is diagnosable:

```csharp
Type emType = FindArtechEntityManager();
if (emType == null)
    return new MoveResult { Ok = false, Error = "EntityManager type not found in loaded assemblies" };
Logger.Info("[Move] Resolved EntityManager as " + emType.FullName);
```

**Verify**: build → exit 0.

### Step 2: Prefer the most specific `FindMethod` overload

In `FindMethod` (lines 60-81), first look for an overload with the exact parameter
count that also satisfies the type checks; fall back to the current behavior only if
none matches. Minimal change: gather all name-matching methods, prefer the one whose
parameter count equals the number of non-null supplied arguments, then fall back to
the existing first-compatible pick:

```csharp
private static MethodInfo FindMethod(Type emType, string name, object arg0, object arg1 = null)
{
    MethodInfo fallback = null;
    foreach (var mi in emType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
    {
        if (mi.Name != name) continue;
        var ps = mi.GetParameters();
        if (ps.Length < 1) continue;
        if (!ps[0].ParameterType.IsInstanceOfType(arg0)) continue;
        if (arg1 != null)
        {
            if (ps.Length < 2 || !ps[1].ParameterType.IsInstanceOfType(arg1)) continue;
            return mi; // exact-arity match with both args compatible — most specific
        }
        if (fallback == null) fallback = mi;
    }
    return fallback;
}
```

(Behavior preserved for the `arg1 != null` case; for the single-arg `UpdateParent`
case, exact-arity means `ps.Length == 1` — if you want stricter arity there too,
prefer the `ps.Length == 1` overload over a wider one; keep it simple and documented.)

**Verify**: build → exit 0.

### Step 3: Tests

Create `src/GxMcp.Worker.Tests/ObjectMoverTests.cs` (reflection-into-internal-static
style, model on `Issues70To72RegressionTests`):

1. **Namespace filter accepts Artech namespaces**: reflectively call
   `FindTypeBySimpleNameInNamespace` with a lambda, or — cleaner — extract the
   predicate as `internal static bool IsArtechNamespace(string ns)` and test:
   - `"Artech.Udm.Framework"` → true
   - `"Artech.Layers.BL"` → true
   - `"Artech"` → true
   - `"System"`, `""`, `"MyApp.EntityManager"` → false
2. **Simple-name fallback prefers the first Artech type**: if feasible in the test
   harness, define two dynamic types named `EntityManager` in different namespaces
   (one `Artech.*`, one `System.*`) via `Reflection.Emit` (the pattern already exists
   in `Issues70To72RegressionTests.CreateFakeLayoutPart`) and assert the Artech one
   is chosen; if dynamic-type assembly loading proves flaky in the net48 test host,
   test the predicate directly (case 1) and document why the full resolution test was
   skipped.
3. **FindMethod prefers exact-arity** (if the method becomes testable with two fake
   types): with two overloads (one 2-param compatible, one 1-param), the 2-param call
   picks the 2-param overload. If constructing fake overload sets is impractical,
   assert instead that `FindMethod` returns non-null for a known method on a real
   CLR type (e.g. `object.ToString` variants) to pin the helper's contract.

**Verify**: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~ObjectMoverTests"` → all pass.

### Step 4: Full worker test suite

**Verify**: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` → all pass.

## Test plan

- `ObjectMoverTests.cs`: `IsArtechNamespace` predicate cases; simple-name-fallback
  prefers Artech when dynamically feasible; `FindMethod` contract pinned.
- Pattern: `Issues70To72RegressionTests` reflection + dynamic-assembly style.
- Verification: worker suite green.

## Done criteria

ALL must hold:
- [ ] The bare `FindFirstType("EntityManager")` call is gone; the fallback is namespace-constrained to `Artech*`.
- [ ] The resolved EntityManager type is logged (`[Move] Resolved EntityManager as ...`).
- [ ] `FindMethod` prefers an exact-arity compatible overload before the looser fallback.
- [ ] New tests present and passing (predicate at minimum; resolution test if harness permits).
- [ ] Worker test suite green.
- [ ] Only the in-scope files modified (`git status`).
- [ ] `plans/README.md` status row updated.

## STOP conditions

- "Current state" excerpts don't match live code (drift).
- A legitimate SDK build resolves `EntityManager` under a **non-Artech** namespace
  (i.e. the constraint would break a working move) — STOP and report the actual
  namespace so the filter can be widened deliberately.
- `ReflectionTypeLoadException` handling in the new scan changes behavior vs the old
  `FindFirstType` in a way that hides a previously-resolvable type — verify the
  `rtle.Types` catch is preserved exactly.
- A step verification fails twice after a reasonable fix.

## Maintenance notes

- The post-move verification in `ObjectService.MoveObject`
  (`MoveNotPersisted`, `ObjectService.cs:1567-1572`) remains the real correctness
  backstop; this plan reduces the chance of reaching it via a wrong-type binding and
  makes the binding diagnosable when it does.
- If the SDK ever moves `EntityManager`, extend the `preferred` FQN list — do not
  loosen the namespace filter.
- `WebFormSaveDiagnostics` (cited in the ObjectMover comment as using the same
  lookup) is not touched; if it still uses a bare simple-name scan, consider aligning
  it in a future pass (noted, not fixed here).
- Reviewer should confirm: the fallback chain still returns a clear
  `EntityManager type not found` MoveResult when nothing Artech-namespaced matches,
  and that the two exact-FQN probes run before the constrained scan.
