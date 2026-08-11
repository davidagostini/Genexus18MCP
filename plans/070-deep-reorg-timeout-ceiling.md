# Plan 070: Give `genexus_db deep=true` (reorg_impact / reorg_preview) a sync ceiling that matches its real runtime

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat e756dd2..HEAD -- src/GxMcp.Gateway/Program.WorkerLifecycle.cs src/GxMcp.Worker/Services/ReorgImpactService.cs src/GxMcp.Gateway/tool_definitions.json src/GxMcp.Gateway.Tests/ReorgImpactPreviewTests.cs`
> If any changed since this plan was written, compare the "Current state"
> excerpts against live code before proceeding; on a mismatch, STOP.

## Status

- **Priority**: P2
- **Effort**: S–M
- **Risk**: LOW–MED
- **Depends on**: none (touches `Program.WorkerLifecycle.cs` — if 069 is executed
  first, that file will differ but at disjoint locations; re-check the drift excerpt)
- **Category**: availability / UX
- **Planned at**: commit `e756dd2`, 2026-08-10

## Why this matters

`genexus_db action=reorg_impact deep=true` and `action=reorg_preview deep=true` run
`ISpecifierService.ImpactDatabase(model, options)` — the tool's own doc comment calls
it "build-heavy" and "specification, build-heavy" (`ReorgImpactService.cs:22-23, 30`).
On a real KB, impact analysis can run for minutes. But `GetToolTimeoutMs` has no
case for `genexus_db`, so it falls through to the **60-second default**
(`Program.WorkerLifecycle.cs:624`). The result: the gateway returns a spurious
timeout error to the agent while the worker keeps running the spec for minutes —
and every later call to that KB queues behind the still-running STA call. The agent
thinks the tool failed, then may retry (stacking another spec run) or give up on a
legitimately slow analysis.

The gateway already has the precedent: `genexus_analyze`, `genexus_test`, and
`genexus_lifecycle` get 600s (`Program.WorkerLifecycle.cs:563-568`); `gxserver`
gets 600s; `apply_pattern` gets 330s with a cushion. `genexus_db deep=true` is the
same class of "legitimately long synchronous SDK call" and deserves the same
treatment, plus an explicit note in the response that deep analysis is expected to
be slow.

## Current state

`src/GxMcp.Gateway/Program.WorkerLifecycle.cs:563-624` — `GetToolTimeoutMs`:

```csharp
internal static int GetToolTimeoutMs(string? toolName, JObject? args)
{
    if (toolName == "genexus_lifecycle" || toolName == "genexus_analyze" || toolName == "genexus_test")
    {
        return 600000;
    }
    // genexus_gxserver update/commit ... return 600000;
    // genexus_edit part=... → 180000
    // genexus_import_object part=... → 300000
    // genexus_apply_pattern → reapplyMs + 30000
    return 60000;          // <-- genexus_db lands here, deep or not
}
```

`src/GxMcp.Worker/Services/ReorgImpactService.cs:211-228` (Preview deep block) and
`:720-746` (Run deep block) both invoke the spec without any progress signal:

```csharp
var analysis = spec.ImpactDatabase(model, options);
deepBlock["deepAnalysis"] = analysis.ToString();
deepBlock["requiresReorganization"] = ...;
```

### Convention

C# (.NET 8 gateway / .NET Framework 4.8 worker), match the surrounding file style.
Tests: xUnit. `GetToolTimeoutMs` is `internal static` — call it directly from tests
(the pattern used for `AsyncEditWatchdogMs` in `AsyncJobWatchdogTests`).

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | exit 0 |
| Build worker | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Run one test file | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~ReorgImpactPreviewTests"` | all pass |
| Full gateway tests | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj` | ~760 pass |
| Worker reorg tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~ReorgImpactPreviewTests|FullyQualifiedName~ReorgPreviewTests"` | all pass |

## Scope

**In scope**:
- `src/GxMcp.Gateway/Program.WorkerLifecycle.cs` — a `genexus_db` case in
  `GetToolTimeoutMs` that returns a generous ceiling when `deep=true` (mirroring the
  600s `genexus_analyze` precedent) and keeps the 60s default for the cheap
  non-deep actions.
- `src/GxMcp.Worker/Services/ReorgImpactService.cs` — a `note`/`hint` on the deep
  result telling the agent this step is spec-heavy and can take minutes (so the long
  response is expected, not a hang), in both `Run` and `Preview`.
- Tests: a small new test file (or extend an existing gateway test file) asserting
  `GetToolTimeoutMs` behavior for `genexus_db` deep vs non-deep.

**Out of scope**:
- Routing `deep=true` through the async-job machinery (background job + poll) — that
  is a larger change and this plan's ceiling + note already removes the spurious
  failure. Noted in Maintenance notes as the eventual replacement if deep calls
  routinely exceed 10 minutes.
- Making `ImpactDatabase` cancellable via `CancellationToken` — the SDK call is
  non-preemptible; the cancellation fan-out (`Control:Cancel`) cannot stop it.
- The non-deep timestamp-heuristic path (`IModelInformationService` timestamps) —
  it's cheap and stays at 60s.

## Git workflow

- Branch: `advisor/070-deep-reorg-timeout-ceiling`
- Commit style: `fix(gateway): give genexus_db deep=true a 10-min sync ceiling; note expected runtime`
- Do NOT push or open a PR unless instructed.

## Steps

### Step 1: `genexus_db` case in `GetToolTimeoutMs`

In `Program.WorkerLifecycle.cs`, before the `return 60000;` fallthrough, add:

```csharp
// genexus_db action=reorg_impact deep=true / action=reorg_preview deep=true runs
// ISpecifierService.ImpactDatabase — specification, build-heavy, can take minutes
// on a real KB. The cheap non-deep actions (timestamps, structure diff) are fast,
// so only the deep flag buys the generous ceiling (same precedent as analyze/test).
if (string.Equals(toolName, "genexus_db", StringComparison.OrdinalIgnoreCase)
    && args?["deep"]?.ToObject<bool?>() == true)
{
    return 600000;
}
```

**Verify**: `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` → exit 0.

### Step 2: Surface the expected runtime in the worker response

In `ReorgImpactService.cs`, the deep blocks already build a `deepBlock`/`deepAnalysis`
object. Add a top-level note when `deep` was requested, in **both** `Run` (around
line 240-258) and `Preview` (around line 250-258). Example (place next to the existing
`hint` property in the result payload):

```csharp
if (deep)
{
    result["deepNote"] = "deep=true ran the SDK's ImpactDatabase specification, which is "
        + "build-heavy and can take several minutes on a large KB. The gateway allows up to "
        + "10 minutes for this call, so a slow response is expected — do not retry unless it "
        + "errors. Use deep=false for the fast timestamp heuristic.";
}
```

(Adjust wording to the surrounding envelope style; keep the `deepNote` key name or
pick a consistent one.)

**Verify**: `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → exit 0.

### Step 3: Tests

Add a focused test file (or extend the nearest gateway test file — check whether
`ReorgImpactPreviewTests` in `src/GxMcp.Gateway.Tests/` exists; if it doesn't, create
`ToolTimeoutCeilingTests.cs` there following `AsyncJobWatchdogTests`' direct-call
style):

- **genexus_db deep=true → 600000**: `Program.GetToolTimeoutMs("genexus_db", new JObject { ["action"] = "reorg_impact", ["deep"] = true }) == 600000`.
- **genexus_db deep=true reorg_preview → 600000**: same with `action = reorg_preview`.
- **genexus_db without deep → 60000**: `new JObject { ["action"] = "reorg_impact" }` → 60000 (and `["deep"] = false` → 60000).
- **Other genexus_db actions unchanged** → 60000.

For the worker-side `deepNote`, add an assertion in the existing worker
`ReorgImpactPreviewTests` / `ReorgPreviewTests` only if they already construct a
`ReorgImpactService` with a fake model; if the deep path is live-KB-gated
(`LiveKbFact`), skip the worker test and rely on the gateway tests + manual
verification note below.

**Verify**: gateway tests for the new file pass; worker reorg tests pass.

### Step 4: Full gate

**Verify**: `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj` → all pass; worker reorg test files pass.

## Test plan

- Gateway: `GetToolTimeoutMs` matrix for `genexus_db` (deep/preview-deep/non-deep/other-action).
- Worker: `deepNote` present on deep responses (if the existing harness allows); otherwise covered by gateway tests + manual verification.
- Pattern: direct `Program.GetToolTimeoutMs` calls (see `AsyncJobWatchdogTests`).
- Verification: gateway + worker suites green.

## Done criteria

ALL must hold:
- [ ] `GetToolTimeoutMs("genexus_db", {action, deep:true}) == 600000` for reorg_impact AND reorg_preview.
- [ ] Non-deep `genexus_db` calls still get 60000.
- [ ] Deep responses carry a note that the run is spec-heavy and can take minutes.
- [ ] New tests present and passing.
- [ ] Gateway suite green; worker reorg tests green.
- [ ] Only the in-scope files modified (`git status`).
- [ ] `plans/README.md` status row updated.

## STOP conditions

- "Current state" excerpts don't match live code (drift).
- A reviewer decides the correct fix is async routing rather than a ceiling (if the
  maintainer changed direction since this plan was written) — STOP and report; the
  plan is written for the ceiling approach per the audit finding.
- `args?["deep"]?.ToObject<bool?>()` behaves differently than excerpted (e.g. the
  gateway strips `deep` before the worker sees it — check `ConvertToolCall` arg
  handling; the ceiling fix reads the gateway-side `args`, which is correct).
- A step verification fails twice after a reasonable fix.

## Maintenance notes

- The 600s ceiling matches `genexus_analyze`/`genexus_test`/`genexus_lifecycle`. If
  deep calls routinely exceed 10 minutes on large KBs, the real fix is routing
  `deep=true` through the async-job machinery (the same path async edits use) so the
  agent gets a job id to poll instead of a long synchronous wait — this plan is the
  stopgap.
- The worker-side deep call remains non-preemptible: a `Control:Cancel` fan-out
  cannot stop `ImpactDatabase`. The ceiling only prevents the *premature* failure;
  the wedged-detection path (`WorkerProcess.cs`) still bounds a truly hung spec at
  `WedgedCommandTimeoutMinutes`.
- If a future tool adds another `deep=` style flag to `genexus_db`, extend the
  timeout case deliberately — don't blanket-raise `genexus_db` (the cheap actions
  should stay fast-failing).
