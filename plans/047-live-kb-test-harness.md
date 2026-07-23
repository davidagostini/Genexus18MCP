# Plan 047: Live-KB test harness (design + spike)

> **Executor instructions**: This is a DESIGN/SPIKE plan. The deliverable is a
> decision + a minimal working harness for ONE representative test, plus a doc of
> open questions — NOT full coverage of every build-only path. Follow the steps; if a
> step reveals the approach is infeasible, STOP and report. Update the status row in
> `plans/README.md` when done.
>
> **Drift check (run first)**: `git diff --stat cf736ec..HEAD -- src/GxMcp.Gateway.Tests/E2ELiveSmokeTests.cs`

## Status

- **Priority**: P2
- **Effort**: L (spike scoped to one path; full rollout is follow-up)
- **Risk**: LOW (adds test infra; touches no production code)
- **Depends on**: none
- **Category**: tests / direction
- **Planned at**: commit `cf736ec`, 2026-07-23

## Why this matters

Repeatedly across audit passes, plans shipped **build-only** — the code compiles and
unit tests pass, but the SDK-live behavior (open a KB, apply a pattern, retype a
variable, run a pipeline) is never exercised automatically because the test suites
have no opened Knowledge Base. Plans 023, 025, 030, and 043 all carry an explicit
"build-only, needs a live KB" caveat; `PatternApplyServiceTests.cs` and
`PatternParityHarnessTests.cs` have TODOs blocked on "a fixture KB layout." Every
SDK-touching change therefore ships with a coverage hole. A committed (or reliably
provisioned) fixture KB plus a harness that runs the already-gated live tests would
convert that recurring hole into real coverage and de-risk the whole SDK surface.

## Current state

- The gating seam already exists: `E2ELiveSmokeTests.cs:10` — "LiveKbFact gates on
  `GXMCP_TEST_KB`. Locally, set the env…". There is a `LiveKbFact` attribute that
  skips when the env var is unset (that is why 7 E2E tests show as skipped in every
  run). Find it: `grep -rn "class LiveKbFact\|GXMCP_TEST_KB" src/GxMcp.Gateway.Tests src/GxMcp.Worker.Tests`.
- Worker tests already use an in-memory fixture for call-graph work:
  `TestFixtures.SmallCallGraph()` (used in `BuildServiceTests`/`CallerGraphServiceTests`).
  That is NOT an opened SDK KB — it's a synthetic index. The gap is a real opened KB.
- The Worker cannot build on GitHub-hosted runners (it references local
  `Artech.*` GeneXus 18 DLLs) — see `release.ps1` / AGENTS.md. So any live-KB harness
  is **local or self-hosted**, never `ubuntu-latest`.
- `genexus_sandbox action=create from=<alias>` already clones a KB on the filesystem
  (`SandboxCopyHelper`), and `genexus_kb action=open path=<path>` opens one — the
  building blocks for provisioning a throwaway KB exist.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Set SDK path | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | (none) |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | `0 Erro(s)` |
| Run a live test | `$env:GXMCP_TEST_KB='<kb path>'; dotnet test ... --filter "FullyQualifiedName~E2ELiveSmoke"` | the 7 skips become runs |

## Scope

**In scope (spike deliverables)**:
- `docs/live-kb-test-harness.md` — the decision doc (fixture source, provisioning, CI story, rollout).
- A minimal proof: make ONE currently-skipped `LiveKbFact` E2E test actually run
  green against a provisioned fixture KB, documenting the exact provisioning steps.
- Optionally a helper (`GxMcp.*.Tests/LiveKb/FixtureKb.cs`) that resolves/provisions the fixture KB path.

**Out of scope**:
- Converting all build-only plans to live tests (that's the follow-up rollout the doc proposes).
- Any production (`src/GxMcp.Worker`, `src/GxMcp.Gateway`) code change.
- A GitHub-hosted CI job (impossible — SDK not on the runner).

## Steps

### Step 1: Decide the fixture-KB source (the core decision)

Evaluate and pick ONE, recording trade-offs in the doc:
- **(a) Commit a tiny KB into the repo** (`test-fixtures/kb/`): reproducible, zero setup,
  but a GeneXus KB is many binary files + may bloat the repo and pin a GX version.
- **(b) Provision on demand** from a known KB via `genexus_sandbox`/a script at test
  start: no repo bloat, but needs a seed KB present on the machine.
- **(c) Document a required local KB path** the developer sets via `GXMCP_TEST_KB`
  (what exists today) + a script that creates a minimal KB if absent.
  Recommendation: lean (b) or (c) given the Worker-is-local-only constraint; (a) only
  if a genuinely minimal KB is small enough. Justify the pick with evidence (measure a
  minimal KB's on-disk size before choosing (a)).

**Verify**: doc §1 states the chosen source + why, with the measured KB size if relevant.

### Step 2: Make ONE live test run green

With the chosen fixture provisioned and `GXMCP_TEST_KB` set, run the existing
`E2ELiveSmokeTests` and get at least one previously-skipped `LiveKbFact` test to
execute and pass. Document the exact provisioning commands.

**Verify**: `$env:GXMCP_TEST_KB=...; dotnet test ... --filter "FullyQualifiedName~E2ELiveSmoke"` → at least one test runs (not skipped) and passes; paste the run summary.

### Step 3: Write the rollout doc

In `docs/live-kb-test-harness.md`: the fixture decision, provisioning steps, how a
local dev / self-hosted CI runs the live suite, and a prioritized list of the
build-only paths (plans 023/025/030/043 behaviors + pattern-apply parity) to convert
next, each with the assertion it should make.

**Verify**: doc has fixture decision + provisioning + rollout list.

## Done criteria

- [ ] `docs/live-kb-test-harness.md` exists with the fixture decision + provisioning + rollout list.
- [ ] At least one `LiveKbFact` test runs (not skipped) and passes with the documented setup.
- [ ] No production code modified (`git status` shows only test/doc/fixture files).
- [ ] `plans/README.md` status row updated.

## STOP conditions

- Provisioning a KB the Worker can open requires GeneXus IDE interaction that can't be
  scripted headlessly — report; the harness may have to be "developer opens KB once,
  sets `GXMCP_TEST_KB`."
- The minimal KB is too large to commit AND no seed KB can be assumed present — report;
  the answer is likely a documented local-path contract, not committed fixtures.

## Maintenance notes

- This unblocks converting build-only plans (043 rollback re-bind, 030 rename atomicity,
  025 pipeline errors, 023 WWP resolve) to real coverage — track those as follow-ups.
- Reviewer: confirm the harness never runs on GitHub-hosted CI (would fail — no SDK) and
  that `GXMCP_TEST_KB`-unset still skips cleanly (green suite for contributors without a KB).
