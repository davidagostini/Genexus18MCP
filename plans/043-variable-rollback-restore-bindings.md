# Plan 043: Restore SDT/BC/built-in bindings in modify-variable rollback

> **Executor instructions**: Follow this plan step by step. Run every verification
> command and confirm the expected result before moving on. If anything in "STOP
> conditions" occurs, stop and report — do not improvise. When done, update the
> status row for this plan in `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 4082fd3..HEAD -- src/GxMcp.Worker/Services/WriteService.Variables.cs src/GxMcp.Worker/Helpers/VariableInjector.cs`
> If either changed, compare "Current state" excerpts to live code; on a mismatch, STOP.

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: MED
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `4082fd3`, 2026-07-23

## Why this matters

`genexus_variable action=modify` (issue #45/#46, shipped v2.31.x) retypes a variable
by removing it and re-adding it with the new type. If the re-save fails (e.g. the SDK
rejects a retype because the variable is bound to layout controls), the catch block
"rolls back" by reconstructing the original variable — but it only copies the
**primitive** fields (`Type`/`Length`/`Decimals`/`DomainBasedOn`). It does **not**
re-establish an SDT, Business Component, or built-in GeneXus data-type binding
(`HttpClient`, `WebSession`, an SDT, a BC, …). So a variable that started as
`&http: HttpClient` and fails a retype is silently rolled back to a bare scalar,
while the tool reports "the original variable was restored." That is data loss masked
as a safe rollback — and it hits exactly the type categories this release added.

## Current state

- `src/GxMcp.Worker/Services/WriteService.Variables.cs`, the modify path:
  - Snapshot + preserved description before the retype (`:837-860`):
    ```csharp
    string preservedDescription = null;
    try { preservedDescription = existing.Description; } catch { }
    // ...
    global::Artech.Genexus.Common.Variable originalSnapshot = existing;
    ```
  - Forward binding of the NEW variable (`:914-924`) — the pattern to mirror in rollback:
    ```csharp
    VariableInjector.BindVariableToSdt(newVar, targetObj);      // SDT
    // ...
    VariableInjector.BindVariableToBC(newVar, targetObj);       // Business Component
    // ...
    else if (VariableInjector.TryBindGenexusDataType(newVar, resolvedTypeForSdk)) { }  // built-in
    ```
  - The rollback catch (`:990-1010`) — restores primitives only:
    ```csharp
    catch (Exception ex)
    {
        try
        {
            if (!varPart.Variables.Any(v => string.Equals(v.Name, varName, ...)))
            {
                var restored = new global::Artech.Genexus.Common.Variable(varPart);
                restored.Name = varName;
                try { if (preservedDescription != null) restored.Description = preservedDescription; } catch { }
                try { restored.Type = originalSnapshot.Type; } catch { }
                try { restored.Length = originalSnapshot.Length; } catch { }
                try { restored.Decimals = originalSnapshot.Decimals; } catch { }
                try { if (originalSnapshot.DomainBasedOn != null) restored.DomainBasedOn = originalSnapshot.DomainBasedOn; } catch { }
                varPart.Variables.Add(restored);
            }
        }
        catch { }
        // ...error envelope...
    }
    ```
- `src/GxMcp.Worker/Helpers/VariableInjector.cs` — the bind helpers to reuse:
  - `public static void BindVariableToSdt(Variable v, KBObject sdtObj)` (`:545`)
  - `public static bool TryBindGenexusDataType(Variable v, string typeName)` (`:607`)
  - `public static void BindVariableToExternalObject(Variable v, string typeName, int subtype)` (`:631`)
  - `public static void BindVariableToBC(Variable v, KBObject bcObj)` (`:733`)
- The read side already resolves a variable's type into a name (per the v2.31.0
  changelog: `ResolveTypeRepresentation` honors `DataTypeString` for
  `GX_EXTERNAL_OBJECT`). Find it: `grep -n "ResolveTypeRepresentation\|DataTypeString" src/GxMcp.Worker/Services/WriteService.Variables.cs src/GxMcp.Worker/Helpers/VariableInjector.cs`.

- Convention: every SDK access is wrapped in `try { } catch { }` (best-effort);
  rollback must not throw. Match it.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Set SDK path | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | (none) |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | `0 Erro(s)` |
| Worker tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~Variable|FullyQualifiedName~Issue33" -v:minimal` | all pass |

## Scope

**In scope**:
- `src/GxMcp.Worker/Services/WriteService.Variables.cs` (snapshot capture + rollback)
- `src/GxMcp.Worker.Tests/Issue33WebSessionAndSdtCollectionTests.cs` (the existing
  home for variable-type tests) — add a rollback test if it can be exercised without a live KB.

**Out of scope**:
- The forward retype/bind path (`:876-924`) — do not change how the new type is applied.
- `VariableInjector` bind helpers themselves — reuse, don't modify.
- The `add`/DSL variable paths — only the `modify` rollback.

## Steps

### Step 1: Capture the original binding BEFORE the variable is removed

Where the snapshot is taken (`:837-860`, before the `Remove`/re-add), capture enough
to replay the original binding. Add locals alongside `originalSnapshot`:

- `string originalTypeName` — the original variable's resolvable type name for a
  non-primitive (SDT / BC / built-in / external object). Obtain it with the SAME
  resolver the read path uses (the `ResolveTypeRepresentation`/`DataTypeString`
  mechanism you located above), computed against `existing` before removal.
- If the resolver distinguishes SDT/BC (needs the KBObject) from built-ins (needs a
  type-name string), capture the resolved `KBObject` reference too
  (`KBObject originalBoundObject`) so you can call `BindVariableToSdt`/`BindVariableToBC`.

Wrap each capture in `try { } catch { }` (best-effort, like the surrounding code).
If the original was a plain primitive/domain variable, `originalTypeName` stays null
and rollback behaves exactly as today.

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → `0 Erro(s)`.

### Step 2: Replay the binding in the rollback branch

After the existing primitive restore (`:1002-1006`) and before `varPart.Variables.Add(restored)`,
add a best-effort re-bind that mirrors the forward path:

```csharp
try
{
    if (originalBoundObject != null && /* original was an SDT */)
        VariableInjector.BindVariableToSdt(restored, originalBoundObject);
    else if (originalBoundObject != null && /* original was a BC */)
        VariableInjector.BindVariableToBC(restored, originalBoundObject);
    else if (!string.IsNullOrEmpty(originalTypeName))
        VariableInjector.TryBindGenexusDataType(restored, originalTypeName);
}
catch { /* best-effort — rollback must not throw */ }
```

Use whatever discriminator the read-path resolver gives you to choose SDT vs BC vs
built-in (e.g. the resolved category, or which capture in Step 1 succeeded). Keep the
whole block inside the existing outer `try/catch` so a bind failure still leaves the
scalar-restored variable rather than throwing out of the catch.

**Verify**: build `0 Erro(s)`.

### Step 3: Update the rollback message to be honest about partial restores

The error envelope hint (`:1020`) says "the original variable was restored." Keep it,
but if you could NOT recover a non-primitive binding (originalTypeName was set but the
re-bind threw), append a caveat to the hint like: "the original had a non-primitive
type; verify its binding with genexus_read part=Variables." Only add the caveat on the
uncertain path — don't scare the common primitive case.

**Verify**: build `0 Erro(s)`.

### Step 4: Test (only if exercisable without a live SDK)

In `Issue33WebSessionAndSdtCollectionTests.cs`, add a test only if the existing tests
there construct variables/parts without a live opened KB (several resolver/masker
tests do). If a genuine failing-retype-on-SDT scenario needs a live SDK the suite
lacks, DO NOT fake it — instead:
- Add a unit test for any NEW pure helper you extracted in Step 1 (e.g. a
  `CaptureOriginalTypeName(existing)` function) if you extracted one, and
- Record in the PR / plans note that the end-to-end rollback path is build-only +
  needs live-KB smoke (follow the precedent of plans 023/025/030 which shipped
  build-only for SDK-live behavior).

**Verify**: `dotnet test ... --filter "FullyQualifiedName~Variable|FullyQualifiedName~Issue33"` → all pass.

## Test plan

- If feasible: a modify-rollback test asserting a non-primitive original is not
  silently downgraded. Otherwise: unit test of the extracted capture helper + a
  build-only note for the SDK-live path.
- Pattern: `Issue33WebSessionAndSdtCollectionTests` resolver tests.
- Verify: filtered worker suite green.

## Done criteria

- [ ] `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj` exits 0.
- [ ] Rollback branch calls `BindVariableToSdt`/`BindVariableToBC`/`TryBindGenexusDataType`
      (grep: `grep -n "Bind" src/GxMcp.Worker/Services/WriteService.Variables.cs` shows a
      bind call inside the catch block around `:1007`).
- [ ] Filtered worker suite green (new test if exercisable; otherwise build-only note recorded).
- [ ] No files outside scope modified.
- [ ] `plans/README.md` status row updated.

## STOP conditions

- The snapshot/forward-bind/rollback excerpts don't match live code (drift).
- The read-path type resolver (`ResolveTypeRepresentation`/`DataTypeString`) cannot
  produce a name/object that `TryBindGenexusDataType`/`BindVariableTo*` can consume
  for the original type — report this; a wrong re-bind is worse than the current
  scalar restore, so do not guess a type name.
- Re-binding in rollback throws in a way the outer catch can't contain (it should —
  everything is `try/catch` — but if the SDK state after `Remove` makes `restored`
  un-bindable, report rather than leaving the variable in a half-bound state).

## Maintenance notes

- This rollback now depends on the read-path type resolver. If that resolver's output
  format changes (new type categories, different `DataTypeString` encoding), the
  capture in Step 1 must be revisited.
- Reviewer: confirm the primitive-only path is byte-identical to today (the fix must
  be purely additive for plain scalar/domain variables) and that the catch still
  cannot throw.
- Follow-up: the forward path and this rollback now duplicate the SDT/BC/built-in
  bind selection logic. If a third caller appears, extract a single
  `ApplyOriginalBinding(variable, captured)` helper.
