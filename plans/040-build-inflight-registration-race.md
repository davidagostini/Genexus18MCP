# Plan 040: Close the build "already running" TOCTOU race

> **Executor instructions**: Follow this plan step by step. Run every verification
> command and confirm the expected result before moving on. If anything in "STOP
> conditions" occurs, stop and report — do not improvise. When done, update the
> status row for this plan in `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 4082fd3..HEAD -- src/GxMcp.Worker/Services/BuildService.cs`
> If `BuildService.cs` changed since this plan was written, compare the "Current
> state" excerpts against the live code before proceeding; on a mismatch, treat it
> as a STOP condition.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `4082fd3`, 2026-07-23

## Why this matters

`genexus_lifecycle action=build` rejects a second build while one is running
(issue #42 P3c) because two concurrent GeneXus `IdeWebBuildAndDeploy` passes race
the generated output. The guard reads the in-flight set, but a build only registers
itself into that set **on a background thread-pool thread inside `RunBuild`**, well
after `Build()` has returned `Accepted`. A second `Build()` call that arrives in
that window sees an empty in-flight set and is admitted — so two MSBuild passes run
at once, exactly the corruption the guard exists to prevent. Registering the build
synchronously in `Build()` before scheduling the background task closes the window.

## Current state

- `src/GxMcp.Worker/Services/BuildService.cs` — the build service. Relevant sites:
  - **The guard** (`Build()`), reads the in-flight set via `GetActiveBuilds`:
    ```csharp
    // BuildService.cs:1028-1047
    if (!string.Equals(Environment.GetEnvironmentVariable("GXMCP_ALLOW_CONCURRENT_BUILDS"), "1", ...))
    {
        var active = GetActiveBuilds(GetKBPath()).FirstOrDefault();
        if (active != null)
        {
            return JsonConvert.SerializeObject(new { status = "BuildAlreadyRunning", ... });
        }
    }
    ```
  - **Task creation + scheduling** (`Build()`), synchronous `_tasks` add then async `Task.Run`:
    ```csharp
    // BuildService.cs:1150-1152
    _tasks[taskId] = status;

    Task.Run(() => RunBuild(status, action, targets));
    ```
  - **The ONLY in-flight registration** — inside `RunBuild`, on the thread-pool thread:
    ```csharp
    // BuildService.cs:1929-1934
    if (status?.TaskId != null)
    {
        if (status.KbPath == null) { try { status.KbPath = GetKBPath(); } catch { } }
        _inFlightBuilds[status.TaskId] = status;
    }
    ```
  - **`GetActiveBuilds`** reads `_inFlightBuilds` (NOT `_tasks`), filtered by KB path:
    ```csharp
    // BuildService.cs:1291-1306
    internal static List<BuildTaskStatus> GetActiveBuilds(string kbPath = null)
    {
        var list = new List<BuildTaskStatus>();
        foreach (var t in _inFlightBuilds.Values)
        {
            if (t == null || IsTerminalStatus(t.Status)) continue;
            if (kbPath != null && t.KbPath != null && !string.Equals(t.KbPath, kbPath, ...)) continue;
            list.Add(t);
        }
        return list;
    }
    ```
  - `_inFlightBuilds` is `private static readonly ConcurrentDictionary<string, BuildTaskStatus>` (`:34`).
  - Removal from `_inFlightBuilds` happens at `BuildService.cs:2377` (`TryRemove`) in the RunBuild finally path.

The race: `Build()` checks `_inFlightBuilds` at `:1030` → empty → adds to `_tasks`
at `:1150` (which `GetActiveBuilds` does NOT consult) → schedules `RunBuild` at
`:1152` → returns. `_inFlightBuilds` is not populated until `:1933` runs on the
thread-pool thread. Any `Build()` between `:1152` and `:1933` sees an empty set.

- Convention: this file uses `ConcurrentDictionary` for `_tasks`/`_inFlightBuilds`,
  guards with `Environment.GetEnvironmentVariable(...)` opt-outs, and returns
  hand-built JSON via `JsonConvert.SerializeObject`. Match it.

## Commands you will need

Set `GX_PATH` once per shell (the Worker references the local GeneXus SDK):

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Set SDK path | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | (no output) |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | `0 Erro(s)` |
| Worker tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~BuildService" -v:minimal` | all pass |

(PowerShell. If a build fails with `MSB3027`/`MSB3021` naming `GxMcp.Worker.exe`
locked, a dev worker holds it: `Stop-Process -Name GxMcp.Worker -Force` then retry.)

## Scope

**In scope**:
- `src/GxMcp.Worker/Services/BuildService.cs`
- `src/GxMcp.Worker.Tests/BuildServiceTests.cs` (add a test)

**Out of scope** (do NOT touch):
- The `RunBuild` body beyond the one registration line — leave the existing
  `:1929-1934` block; making it idempotent (dictionary assignment) is fine, do not
  restructure it.
- The gateway-side build routing or `genexus_lifecycle` schema.
- The removal path at `:2377`.

## Git workflow

- Branch: `advisor/040-build-inflight-race`
- Conventional Commits, no co-authorship trailer. Example from `git log`:
  `fix(worker): register in-flight build synchronously to close TOCTOU`

## Steps

### Step 1: Register the build in `_inFlightBuilds` synchronously in `Build()`

In `Build()`, immediately after `_tasks[taskId] = status;` (`:1150`) and BEFORE
`Task.Run(...)` (`:1152`), set the KB path and register the in-flight entry:

```csharp
_tasks[taskId] = status;

// issue #42 (P3c): register the in-flight build here — synchronously, before the
// background task is scheduled — so a second Build() call cannot slip through the
// "already running" guard during the window before RunBuild's own registration runs.
if (status.KbPath == null) { try { status.KbPath = GetKBPath(); } catch { } }
_inFlightBuilds[status.TaskId] = status;

Task.Run(() => RunBuild(status, action, targets));
```

Leave the existing registration at `:1929-1934` in place — it becomes an idempotent
re-assignment (same key, same value) and still covers the KbPath-late case.

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → `0 Erro(s)`.

### Step 2: Add a regression test

In `src/GxMcp.Worker.Tests/BuildServiceTests.cs`, add a test that a second build is
rejected once the first is registered, driving through the public `Build()` entry.
Model it after the existing tests in that file (they already construct a
`BuildService` and assert on the returned JSON `status`/`code`). The test must:

- Call `Build(...)` once for a target, parse the JSON, assert it is NOT
  `BuildAlreadyRunning` (it should be `Accepted`/running).
- Immediately (no delay — the point is the synchronous window) call `Build(...)`
  again for the same KB and assert the returned JSON `code == "BuildAlreadyRunning"`.
- Follow the existing tests' teardown so `_inFlightBuilds` doesn't leak into other
  tests (if the file has a helper that clears build state, reuse it; otherwise cancel
  the task the way sibling tests do).

If `BuildServiceTests` cannot exercise `Build()` without a live KB/SDK (the first
`Build()` throws before returning `Accepted`), then instead add a **focused test on
the guard's observable contract**: register a status directly via the same code path
the fix uses (a small internal test seam if one exists, or the public `Build()` if it
returns before touching the SDK) and assert `GetActiveBuilds(kbPath)` sees it
synchronously. If neither is possible without an SDK harness the suite lacks, STOP
and report — do not fake the SDK.

**Verify**: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~BuildService" -v:minimal` → all pass, including the new test.

## Test plan

- New test in `BuildServiceTests.cs`: "second concurrent build is rejected
  synchronously" (the happy-path first build + immediate second build → `BuildAlreadyRunning`).
- Structural pattern: the existing `BuildService` tests in the same file.
- Verify: the filtered worker test run above is green with one more test than before.

## Done criteria

- [ ] `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj` exits 0.
- [ ] `_inFlightBuilds[...]` is assigned inside `Build()` before `Task.Run` (grep:
      `grep -n "_inFlightBuilds\[status.TaskId\] = status" src/GxMcp.Worker/Services/BuildService.cs`
      returns TWO sites — the new one in `Build()` and the existing one in `RunBuild`).
- [ ] New regression test exists and passes; filtered `BuildService` suite green.
- [ ] No files outside the in-scope list modified (`git status`).
- [ ] `plans/README.md` status row updated.

## STOP conditions

- The excerpts at `:1030`, `:1150-1152`, `:1929-1934`, or `:1291-1306` don't match
  the live code (drift).
- Registering early causes an existing test to fail because it depended on the entry
  NOT being present until `RunBuild` ran (investigate: it likely needs the same
  teardown the new test uses) — report rather than deleting the assertion.
- You cannot write a test that exercises the guard without standing up a live GeneXus
  SDK/KB the suite doesn't already provide.

## Maintenance notes

- If build dispatch ever moves off the single worker/STA model to truly parallel
  per-KB builds, the KbPath filter in `GetActiveBuilds` becomes load-bearing —
  ensure `status.KbPath` is always set before registration (this fix already does).
- Reviewer: confirm the early registration is removed on every exit path. The
  existing finally at `:2377` (`TryRemove`) covers the normal RunBuild lifecycle; the
  synchronous registration relies on `RunBuild` always running (it's scheduled
  unconditionally right after). If a future change can `return` between the new
  registration and `Task.Run`, it must `TryRemove` first.
