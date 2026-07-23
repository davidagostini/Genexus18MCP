# Plan 045: De-quadratic the referencedButNotBuilt evidence scan

> **Executor instructions**: Follow this plan step by step. Run every verification
> command and confirm the expected result before moving on. If anything in "STOP
> conditions" occurs, stop and report. When done, update the status row in `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 4082fd3..HEAD -- src/GxMcp.Worker/Services/BuildService.cs`
> If it changed, compare "Current state" to live code; on a mismatch, STOP.

## Status

- **Priority**: P3
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: perf
- **Planned at**: commit `4082fd3`, 2026-07-23

## Why this matters

When a build runs with `includeCallees=none`, the build-evidence step lists callees
of the built targets that still lack a generated `.cs` (`referencedButNotBuilt`). The
"already built?" check is a linear `checkList.Any(...)` scan run inside the callee
loop inside the target loop — O(targets × callees × |checkList|). It's bounded today
(callers keep target lists small), but a documented, supported combination — a large
batch `target` list with `includeCallees=none` — pays quadratic cost on every
successful build. A one-line `HashSet` lookup removes it.

## Current state

- `src/GxMcp.Worker/Services/BuildService.cs`, `AttachGenerateEvidence` (the
  `referencedButNotBuilt` block, `:1788-1815`):
  ```csharp
  var referencedButNotBuilt = new JArray();
  bool calleesExcluded = string.Equals(status.BuildPlan?.IncludeCallees, "none", ...);
  if (calleesExcluded && _callerGraphService != null && targets != null && targets.Count > 0)
  {
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var t in checkList)
      {
          string bare = t.Contains(":") ? t.Substring(t.LastIndexOf(':') + 1).Trim() : t;
          List<string> callees = null;
          try { callees = _callerGraphService.GetCallees(bare); } catch { }
          if (callees == null) continue;
          foreach (var c in callees)
          {
              string cbare = c.Contains(":") ? c.Substring(c.LastIndexOf(':') + 1).Trim() : c;
              if (string.IsNullOrEmpty(cbare) || !seen.Add(cbare)) continue;
              if (checkList.Any(x => string.Equals(
                      x.Contains(":") ? x.Substring(x.LastIndexOf(':') + 1).Trim() : x,
                      cbare, StringComparison.OrdinalIgnoreCase))) continue; // already built
              bool hasCs;
              try { hasCs = GeneratedDiffService.FindGeneratedFiles(kbPath, cbare, allRoots: true).Count > 0; }
              catch { hasCs = true; }
              if (!hasCs) referencedButNotBuilt.Add(cbare);
          }
      }
  }
  ```
- The `checkList.Any(...)` at `:1806` recomputes each element's bare name on every
  inner iteration — that's the quadratic cost.

- Convention: the file already uses `HashSet<string>(StringComparer.OrdinalIgnoreCase)`
  (the `seen` set right above). Match it. There is likely a bare-name helper already —
  check for one: `grep -n "BareName\|LastIndexOf(':')" src/GxMcp.Worker/Services/BuildService.cs`.
  If a helper exists, use it; otherwise inline the same `Contains(":") ? Substring : t` logic.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Set SDK path | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | (none) |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | `0 Erro(s)` |
| Worker tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~BuildService|FullyQualifiedName~GeneratedDiff" -v:minimal` | all pass |

## Scope

**In scope**:
- `src/GxMcp.Worker/Services/BuildService.cs` — only the `referencedButNotBuilt` block.

**Out of scope**:
- The rest of `AttachGenerateEvidence`, `GetCallees`, `FindGeneratedFiles`.
- The concurrent-build guard (that is plan 040).

## Steps

### Step 1: Precompute a bare-name HashSet of checkList once

Before the `foreach (var t in checkList)` loop, build:

```csharp
var checkSet = new HashSet<string>(
    checkList.Select(x => x.Contains(":") ? x.Substring(x.LastIndexOf(':') + 1).Trim() : x),
    StringComparer.OrdinalIgnoreCase);
```

Then replace the inner `if (checkList.Any(...)) continue;` (`:1806-1808`) with:

```csharp
if (checkSet.Contains(cbare)) continue; // already built
```

Semantics are identical (case-insensitive bare-name membership), just O(1).

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → `0 Erro(s)`.

### Step 2: Test

If `BuildServiceTests` has a test that exercises `AttachGenerateEvidence` /
`referencedButNotBuilt` (grep the test file for `referencedButNotBuilt`), confirm it
still passes and, if easy, add a case with a batch target list whose callee is already
in the target set → asserts that callee is NOT reported as `referencedButNotBuilt`
(the "already built" branch). If the method is private and only reachable via a live
build, a targeted unit test may not be feasible; then rely on the existing
`BuildService`/`GeneratedDiff` suite staying green and note the equivalence is by
construction (pure set-membership swap).

**Verify**: `dotnet test ... --filter "FullyQualifiedName~BuildService|FullyQualifiedName~GeneratedDiff"` → all pass.

## Test plan

- Reuse/extend existing `BuildService` evidence tests if present; else green suite +
  by-construction argument (identical semantics).
- Verify: filtered worker suite green.

## Done criteria

- [ ] `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj` exits 0.
- [ ] `grep -n "checkList.Any" src/GxMcp.Worker/Services/BuildService.cs` shows the
      `referencedButNotBuilt` occurrence is gone (replaced by `checkSet.Contains`).
- [ ] Filtered worker suite green; no files outside scope modified.
- [ ] `plans/README.md` status row updated.

## STOP conditions

- The block excerpt doesn't match live code (drift).
- `checkList` elements are not strings (they should be) — report.

## Maintenance notes

- Reviewer: confirm the `checkSet` is built from the SAME bare-name transform used in
  the removed `.Any` predicate (case-insensitive, `:`-suffix stripping) so behavior is
  identical.
- If `checkList` ever becomes huge AND `GetCallees` returns huge lists, the remaining
  cost is the per-callee `FindGeneratedFiles` filesystem probe, not this scan.
