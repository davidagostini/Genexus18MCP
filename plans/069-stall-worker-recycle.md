# Plan 069: Recycle the wedged worker when the async-job stall watchdog fires

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat e756dd2..HEAD -- src/GxMcp.Gateway/Program.RequestLoop.cs src/GxMcp.Gateway/Program.WorkerLifecycle.cs src/GxMcp.Gateway/WorkerPool.cs src/GxMcp.Gateway/BackgroundJobRegistry.cs src/GxMcp.Gateway.Tests/AsyncJobWatchdogTests.cs src/GxMcp.Gateway.Tests/WorkerPoolTests.cs`
> If any changed since this plan was written, compare the "Current state"
> excerpts against live code before proceeding; on a mismatch, STOP.

## Status

- **Priority**: P1
- **Effort**: S–M
- **Risk**: MED
- **Depends on**: none (independent of 068; both touch gateway/worker but disjoint files)
- **Category**: availability
- **Planned at**: commit `e756dd2`, 2026-08-10

## Why this matters

Issue #79's stall watchdog converted "async job stuck `running` forever" into a
terminal `stalled` state — but it stops there. When the watchdog fires, the
worker's **STA thread is still inside the blocked SDK call** (typically an IDE
modal dialog holding the model). The job is marked `stalled`, yet nothing recycles
that worker, so **every subsequent call to the KB queues behind the stuck SDK
call**. The stalled envelope's recovery guidance — "re-run the edit synchronously
to get the immediate SDK error" — cannot work: the sync call queues behind the same
stuck thread. The only real escape today is the 15-minute wedged detector
(`WorkerProcess.cs:176-195`, `WedgedCommandTimeoutMinutes` default 15 min +
120s silence), so a blocked write costs the user a 15-minute KB outage and
misleading recovery steps.

The worker-crash path already has exactly the recycle machinery needed: when a
worker exits with a non-skipped reason, `OnWorkerExited` eagerly respawns a
replacement with retries (`Program.WorkerLifecycle.cs:31-150`). The stall path
should deliberately stop the wedged worker with `WorkerStopReason.Wedged` — which is
**not** in the eager-respawn skip list — so the existing crash-recovery loop brings
the KB back in seconds instead of 15 minutes.

## Current state

The watchdog branch (`src/GxMcp.Gateway/Program.RequestLoop.cs:1762-1802`):

```csharp
_ = Task.Run(async () =>
{
    try
    {
        // ... SendWorkerCommandAsync(capturedCmd, 0, ...) raced against watchdogDelay ...
        var completed = await Task.WhenAny(workerTask, watchdogDelay).ConfigureAwait(false);
        if (completed != workerTask)
        {
            var now = JobRegistry.Get(editJob.Id);
            if (now != null && string.Equals(now.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                int boundSeconds = ...;
                JobRegistry.Stall(
                    editJob.Id,
                    capturedName + " did not return within the " + boundText + "...",
                    BuildStalledAsyncMutationEnvelope(editJob.Id, capturedName, estEdit, boundSeconds));
                Log($"[AsyncEdit] Watchdog fired for job={editJob.Id} tool={capturedName} after {watchdogMs}ms — marked stalled.");
            }
            return;                                  // <-- job stalled, worker left wedged
        }
        var inner = await workerTask;                // resolves "crashed/exited" after recycle — Complete() is a no-op on a stalled job
        ...
        JobRegistry.Complete(editJob.Id, ok, ...);
    }
    ...
});
```

Key facts (verified):
- `BackgroundJobRegistry.Complete` and `Cancel` **no-op on a `stalled` job**
  (`BackgroundJobRegistry.cs:83-101, 145-153`) — so recycling the worker and letting
  `workerTask` resolve with "crashed/exited" cannot resurrect or rewrite the stalled
  job. The registry already guards this.
- `WorkerPool.Close(alias)` removes the durable `_known` record — wrong for a stall
  (the user's KB must stay resolvable). `WorkerPool.DropLiveEntry(alias)` stops with
  `WorkerStopReason.ExplicitClose`, which **is** in the eager-respawn skip list
  (`Program.WorkerLifecycle.cs:88-101`), so neither existing method triggers a
  respawn. A new method that stops with `WorkerStopReason.Wedged` and leaves `_known`
  intact is required.
- `OnWorkerExited` respawn skips only
  `IdleTimeout | GatewayShutdown | BusyReject | ExplicitClose | PlannedReload`
  (`Program.WorkerLifecycle.cs:88-95`); `Wedged` is not skipped → eager respawn runs.
- `_currentKb` is an `AsyncLocal` set per request; the `Task.Run` continuation
  captures it via ExecutionContext, but the code should capture the alias into a
  local **before** `Task.Run` for explicitness and testability.
- gxserver update/commit jobs are unbounded (`watchdogMs = int.MaxValue`), so the
  stall branch never fires for them — no guard needed, but assert it in the tests.

### Convention

C# (.NET 8 gateway), match the surrounding file style. Tests: xUnit;
`AsyncJobWatchdogTests` calls `Program.*` internal statics directly; `WorkerPoolTests`
uses the `_testSpawnHook` seam (no real worker processes).

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | exit 0 |
| Run one test file | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~AsyncJobWatchdogTests"` | all pass |
| Run the other | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~WorkerPoolTests"` | all pass |
| Full gateway tests | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj` | ~760 pass |

## Scope

**In scope**:
- `src/GxMcp.Gateway/WorkerPool.cs` — add `RecycleStalledWorker(string alias)` (or
  equivalent): stop the live entry's worker with `WorkerStopReason.Wedged`, keep the
  `_known` record, don't remove the live entry here (let `OnWorkerExited` handle
  cleanup + respawn exactly like the crash path does).
- `src/GxMcp.Gateway/Program.RequestLoop.cs` — on the stall branch, capture the KB
  alias before `Task.Run` and call the recycle; update the log line.
- `src/GxMcp.Gateway/Program.WorkerLifecycle.cs` — update
  `BuildStalledAsyncMutationEnvelope` hint text to tell the agent the worker is being
  recycled and the KB will return in a few seconds (keep the existing "async=true"
  token so `BuildJobResultEnvelope_Stalled_IsErrorWithActionableResult` still passes,
  or update that assertion deliberately).
- `src/GxMcp.Gateway.Tests/AsyncJobWatchdogTests.cs` (extend).
- `src/GxMcp.Gateway.Tests/WorkerPoolTests.cs` (extend).

**Out of scope**:
- Changing the stall *bound* or which tools get a bound (that's issue #79's settled
  design — gxserver stays unbounded).
- `BackgroundJobRegistry` semantics — the no-op-on-terminal behavior is correct and
  is what makes this change safe; don't touch it.
- Actually killing the worker from `WorkerProcess` internals — use the existing
  `StopWithReason(WorkerStopReason.Wedged)` path only.

## Git workflow

- Branch: `advisor/069-stall-worker-recycle`
- Commit style: `fix(gateway): recycle the wedged worker when an async job stalls`
- Do NOT push or open a PR unless instructed.

## Steps

### Step 1: Add `WorkerPool.RecycleStalledWorker`

In `WorkerPool.cs`, near `DropLiveEntry` (line 341), add:

```csharp
// Issue #79 follow-up: an async job that trips the stall watchdog has left the
// worker's STA thread blocked inside an SDK call (typically an IDE modal dialog
// holding the model). Marking the job 'stalled' is not enough — every later call
// to the KB queues behind the stuck thread until the 15-min wedged detector
// kills the worker. Stop it NOW with WorkerStopReason.Wedged (NOT ExplicitClose,
// which is in the eager-respawn skip list) so OnWorkerExited's respawn loop
// brings a fresh worker back in seconds. The durable _known record is kept so
// the KB stays resolvable — unlike Close(), which forgets it.
public void RecycleStalledWorker(string alias)
{
    if (string.IsNullOrWhiteSpace(alias)) return;
    string key = alias.ToLowerInvariant();
    if (_entries.TryGetValue(key, out var entry))
    {
        try { entry.Worker?.StopWithReason(WorkerStopReason.Wedged); } catch { }
    }
}
```

Do **not** remove the entry here — the `OnWorkerExited` handler (`Program.WorkerLifecycle.cs:31-150`)
drops the live entry and respawns with retries, exactly as it does for a crash. If the
worker is already dead, `StopWithReason` is a no-op and `OnWorkerExited` has already
fired — safe either way.

**Verify**: `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` → exit 0.

### Step 2: Call it from the stall branch

In `Program.RequestLoop.cs`, before `_ = Task.Run(...)` (line 1762), capture the
alias (the continuation needs it even if `_currentKb` is cleared):

```csharp
string? stallKbAlias = _currentKb.Value?.NormalizedAlias;
```

Inside the stall branch (after `JobRegistry.Stall(...)`, before `return;`), add:

```csharp
// Issue #79 follow-up: the job is terminal, but the worker's STA thread is still
// inside the blocked SDK call — recycle it so later calls don't queue behind it
// until the 15-min wedged detector fires. The registry no-ops a late worker
// response on a stalled job, so the race is safe (see BackgroundJobRegistry).
try { if (!string.IsNullOrWhiteSpace(stallKbAlias)) _workerPool?.RecycleStalledWorker(stallKbAlias); }
catch (Exception ex) { Log($"[AsyncEdit] Stalled-worker recycle failed for {stallKbAlias}: {ex.Message}"); }
```

Update the existing log line to mention the recycle, e.g.:
`... marked stalled; recycling wedged worker (alias={stallKbAlias}).`

**Verify**: build → exit 0.

### Step 3: Update the stalled envelope's recovery guidance

In `BuildStalledAsyncMutationEnvelope` (`Program.WorkerLifecycle.cs:730-740`), replace
the `hint` with text that reflects the recycle:

```csharp
["hint"] = "The wedged worker for this KB is being recycled automatically — it will "
    + "come back in a few seconds (eager respawn). 1) Wait ~5s, then genexus_read the "
    + "object to confirm whether the write partially persisted. 2) Re-run the edit "
    + "WITHOUT async=true against the fresh worker to get the immediate SDK error. "
    + "3) Check the GeneXus IDE for a waiting modal dialog (e.g. \"object modified "
    + "externally — reload?\") and dismiss it. 4) If the KB does not return, use "
    + "genexus_worker_reload mode=soft force=true."
```

Keep the `"async=true"` substring somewhere in the hint so
`BuildJobResultEnvelope_Stalled_IsErrorWithActionableResult` keeps passing — or
update that test's assertion deliberately if you reword it away (state the change in
the PR).

**Verify**: build → exit 0; then run `AsyncJobWatchdogTests` → all pass (or the
updated assertion passes).

### Step 4: Tests

**`WorkerPoolTests.cs`** — using the existing `_testSpawnHook` seam (see how the
current tests stand up a pool without real processes; `WorkerPoolTests.cs:9-120`):

1. **RecycleStalledWorker stops the live worker** — open a worker for an alias,
   call `RecycleStalledWorker(alias)`, assert the worker was told to stop (hook
   records the stop reason) with `WorkerStopReason.Wedged`.
2. **RecycleStalledWorker keeps the KB resolvable** — after the call, a subsequent
   `AcquireAsync` for the same alias still works (`_known` retained; contrast the
   `Close()` behavior tested at `DropLiveEntry_keeps_known_but_removes_open`,
   `WorkerPoolTests.cs:114-120`).
3. **RecycleStalledWorker unknown alias is a no-op** — does not throw.

**`AsyncJobWatchdogTests.cs`** — add:

4. **Stalled gxserver jobs are never recycled** — `AsyncEditWatchdogMs` returns
   `int.MaxValue` for a gxserver job estimate path is already covered; add an
   assertion that the recycle call site is guarded to edit/variable jobs: since the
   stall branch only executes when `watchdogMs != int.MaxValue`, and gxserver sets it
   to `int.MaxValue`, assert `AsyncEditWatchdogMs` for the gxserver default estimate
   is `int.MaxValue` (pins the "never stalls ⇒ never recycles" invariant).
5. **Stalled envelope mentions the recycle** — assert the envelope `hint` contains
   "recycled" (or whatever wording you chose) and still contains "async=true".

If the harness can't observe the stop reason through `_testSpawnHook`, assert the
side effect that IS observable (worker entry removed from live pool while `_known`
retained, matching the `DropLiveEntry_keeps_known_but_removes_open` pattern) and
document the limitation in the test file.

**Verify**: `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~AsyncJobWatchdogTests|FullyQualifiedName~WorkerPoolTests"` → all pass.

### Step 5: Full gateway test suite

**Verify**: `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj` → all pass.

## Test plan

- `WorkerPoolTests.cs`: recycle stops with `Wedged`, keeps `_known`, no-op on unknown alias.
- `AsyncJobWatchdogTests.cs`: envelope hint mentions the recycle + keeps `async=true`; gxserver stays unbounded (never stalls ⇒ never recycles).
- Pattern: `DropLiveEntry_keeps_known_but_removes_open` + the direct `Program.*` static calls in `AsyncJobWatchdogTests`.
- Verification: gateway test suite green.

## Done criteria

ALL must hold:
- [ ] `WorkerPool.RecycleStalledWorker` exists, stops with `WorkerStopReason.Wedged`, does NOT forget `_known`.
- [ ] The stall branch in `Program.RequestLoop.cs` calls it with the captured alias, guarded by try/catch.
- [ ] `BuildStalledAsyncMutationEnvelope` hint reflects the automatic recycle and still contains `async=true` (or the test was updated deliberately).
- [ ] New tests present and passing.
- [ ] Gateway test suite green.
- [ ] Only the in-scope files modified (`git status`).
- [ ] `plans/README.md` status row updated.

## STOP conditions

- "Current state" excerpts don't match live code (drift).
- `OnWorkerExited`'s respawn does **not** actually run for `WorkerStopReason.Wedged`
  (i.e. the skip-list check or the stop-reason plumbing differs from the excerpt) —
  STOP and report; the recycle would strand the KB with no worker.
- Killing the worker on stall is discovered to be unsafe for a *legitimately slow*
  write (the bound is `max(10 min, 8×est)` and capped at 60 min — if a reviewer
  believes a real write can exceed that, STOP and report; do not raise the bound in
  this plan).
- A step verification fails twice after a reasonable fix.

## Maintenance notes

- The recycle relies on `BackgroundJobRegistry`'s terminal-state guard
  (`Complete`/`Cancel` no-op on `stalled`). If that guard is ever relaxed, revisit
  this plan's safety argument.
- The eager-respawn skip list (`Program.WorkerLifecycle.cs:88-95`) is the
  single source of truth for "which exits get a fresh worker". `Wedged` must stay
  out of that list; `RecycleStalledWorker` depends on it. Add a comment at the skip
  list pointing at this plan so a future edit doesn't silently break the recycle.
- gxserver update/commit remains unbounded by design (server applies can run
  arbitrarily long) — if that ever changes, the recycle guard must be revisited so a
  legitimate server apply is not killed.
- Reviewer should confirm: the stalled job's `Result` envelope (built at Stall time)
  is not overwritten by the late "crashed/exited" worker response (it isn't —
  `Complete` no-ops), and that `_respawnFailures` bookkeeping is untouched.
