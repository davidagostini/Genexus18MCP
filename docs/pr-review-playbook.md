# Fast multi-PR review playbook

Use this playbook when several open PRs must be reviewed, repaired, and merged.
The workflow is deliberately two-phase: inspect and batch all edits first; run
validation after the last edit in each lane, not after every patch.

## Invariants

- One reviewer lane owns one PR and one isolated worktree.
- The coordinator owns `main`, `CHANGELOG.md`, generated fixtures/goldens, and
  merge sequencing.
- Agents may inspect and repair their assigned PR, but they do not merge, create
  a second PR, force-push, close issues, or mutate a Knowledge Base.
- A fix is not complete until the final commit is pushed and the PR head read
  back from GitHub equals the reviewed local `HEAD`.
- A skipped SDK/live lane is unavailable evidence, not a pass.

## 1. Prepare isolated lanes

Start from a clean repository checkout and snapshot the exact remote heads:

```powershell
pwsh -NoProfile -File .\scripts\pr-review-worktrees.ps1 `
  -PullRequest 245,246,247 -Action Prepare
```

The command prints a manifest outside the repository. It creates one detached
worktree per PR, verifies the fetched OID against GitHub, and refuses to reuse a
path or silently review a moved head. Give each agent only its manifest entry.
The parent checkout remains available to the coordinator.

Verify a lane after an agent has committed and pushed:

```powershell
pwsh -NoProfile -File .\scripts\pr-review-worktrees.ps1 `
  -ManifestPath <manifest> -PullRequest <number> -Action Verify
```

`Verify` fails when the worktree is dirty or when the remote PR head differs
from local `HEAD`; this makes an unpushed local fix an incomplete result. Clean
up only after the review batch is finished:

```powershell
pwsh -NoProfile -File .\scripts\pr-review-worktrees.ps1 `
  -ManifestPath <manifest> -Action Cleanup
```

Use `-Force` only when intentionally discarding a dirty review worktree.

## 2. Review without test churn

Each reviewer works in this order:

1. Read the PR description, exact head/base, changed-file summary, comments,
   formal reviews, and checks.
2. Inspect the changed code, callers, contracts, and nearby tests. Use ripwire
   for orientation and blast radius before broad searches.
3. Record all findings and the complete fix set before editing.
4. Apply the complete scoped edit wave, including regression tests and docs.
5. Run one final validation wave after the last edit.

Do not spend a test process after every small edit. A focused test is justified
mid-wave only when it is the cheapest way to distinguish two competing bug
hypotheses or prevent an unsafe destructive action. Otherwise preserve the
iteration budget for the final gate.

Use the existing fork-safe submission gate only after the edit wave is done:

```powershell
pwsh -NoProfile -File .\scripts\pr-push.ps1 `
  -PullRequest <number> -ForceWithLease
```

This rejects dirty/stale branches, runs the integration preflight, pushes the
explicit PR ref, and verifies the published remote head. Do not report a fix
as complete before the `pr-review-worktrees.ps1 -Action Verify` readback.

## 3. Bounded architectural review

Use the scoped wrapper instead of printing an unbounded ripwire report into the
agent context:

```powershell
pwsh -NoProfile -File .\scripts\ripwire-review.ps1 `
  -BaseRef origin/main -QualityDelta -TokenBudget 12000 `
  -OutputDirectory "$env:TEMP\gxmcp-ripwire-review-<id>"
```

The wrapper stores full `pr-context` and scoped `quality-delta` reports outside
the repository and prints only a compact JSON summary. `quality-delta` is
scoped to changed production paths; findings from unrelated tests or historical
code are advisory and do not become blockers merely because the global
`--test-gate` cannot prove coverage for every symbol in this large repository.
Use the affected-test mapping and the final project gates for the real decision.

## 4. Final validation wave

Run this once after all fixes for the lane, with an explicit summary path:

```powershell
pwsh -NoProfile -File .\scripts\integration-preflight.ps1 `
  -BaseRef origin/main -TimeoutSeconds 1200 `
  -SummaryPath "$env:TEMP\gxmcp-pr-<number>-final.json"
```

The preflight is the evidence boundary. Keep its summary path and distinguish
`passed`, `failed`, `skipped`, and `unavailable`; never convert a timeout or
skipped SDK gate into green evidence. The final check ordering is intentionally
cheap static/contract checks first, then Node/PowerShell, then .NET.

## 5. Sequential landing

After the coordinator has verified a PR and its remote checks:

1. Run `scripts/pr-preflight.ps1 -PullRequest <number>` immediately before the
   merge.
2. Merge one PR only.
3. Read back `state`, `merged`, `merged_at`, and the resulting commit.
4. Fast-forward local `main`.
5. Refresh every remaining PR's base/head/mergeability and update only the next
   lane that needs it.
6. Resolve changelog conflicts by combining both Unreleased sections, then run
   the final validation wave on the resulting head before its checks.

Do not prepare all remaining branches against a base that will change during the
batch. Remote CI remains mandatory after each pushed head; avoiding redundant
local test waves does not waive required provider checks.

## 6. Changelog conflict helper

When both sides changed `CHANGELOG.md`, use staged conflict sides or reviewed
copies and preserve the current base document outside `## Unreleased`:

```powershell
pwsh -NoProfile -File .\scripts\merge-unreleased-changelog.ps1 `
  -OursPath .\CHANGELOG.ours.md `
  -TheirsPath .\CHANGELOG.theirs.md `
  -OutputPath .\CHANGELOG.md -Force
```

The helper refuses conflict markers or a missing `## Unreleased`, keeps both
sides' bullet blocks in deterministic order, deduplicates exact entries, and
writes atomically. Inspect the diff before committing the resolution.

## 7. Required lane handoff

Return this machine-readable record:

```text
status: fixed | no_change | blocked | incomplete
pr: <number>
worktree: <absolute path>
headBefore: <sha>
headAfter: <sha or none>
remoteHeadVerified: true | false
cleanWorktree: true | false
editsComplete: true | false
testsAfterLastEdit: <final command and result, or pending>
unrunGates: <commands still required>
changedContracts: <schema/router/fixture/API contracts or none>
changelogEntry: <proposed Unreleased entry or none>
```

`incomplete` is the only valid status when a fix exists locally but is not
committed, pushed, and verified remotely. A test run from before the final edit
is context, not final validation.
