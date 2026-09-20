# Agent coordination

Use this guide when delegating implementation or review lanes. Keep each lane
independently reviewable, use one isolated worktree per lane, and leave shared
artifacts to the coordinator. For several open PRs, follow
[`docs/pr-review-playbook.md`](pr-review-playbook.md).

## Parallel lane contract

- Give every lane an explicit, non-overlapping path allowlist and a dedicated
  worktree created from the exact PR head. Never let parallel lanes share a
  checkout or detached HEAD.
- The coordinator owns `CHANGELOG.md`, generated fixtures/goldens, release
  metadata, lockfiles, and client-registration artifacts. Agents must report
  required changes to those files instead of editing them in parallel.
- An agent may commit and push its isolated functional patch to the exact PR
  head ref when the coordinator delegated that authority, but no lane merges,
  creates a second PR, force-pushes, or closes an issue. If it cannot publish a
  fix, it must report `incomplete`, not `fixed`.
- Preserve unrelated staged and worktree changes when integrating a lane.

## Required handoff

Every lane returns this small record, even when it made no change:

```text
status: fixed | no_change | blocked | incomplete
pr: <number or none>
worktree: <absolute path or none>
headBefore: <sha or none>
headAfter: <sha or none>
remoteHeadVerified: true | false
cleanWorktree: true | false
editsComplete: true | false
testsAfterLastEdit: <final command and result, or pending>
unrunGates: <commands still required>
changedContracts: <schema/router/fixture/API contracts or none>
generatedArtifactsNeeded: <files or none>
changelogEntry: <proposed Unreleased entry or none>
```

`testsAfterLastEdit` is authoritative: a test run from before the final edit
cannot be reported as final validation. During review, inspect findings and
batch the complete edit wave first; run the final gate once after the last edit.
Run an earlier focused test only to distinguish a blocker or prevent an unsafe
operation.

## Coordinator gate

After all lane edits are integrated, run from the repository root:

```powershell
npm run test:integration
```

This is the single final local validation wave for the combined edit set. It
validates the commit/index/worktree diff, rejects conflict markers and
incomplete discovery-contract changes, runs the existing PowerShell/Node gates,
and executes the .NET solution serially with an explicit timeout. Its JSON
summary and phase logs are written outside the repository; keep the printed path
as evidence. Use `-ValidateOnly` during exploration only when no code has been
changed yet; it is not final test evidence.

For a changed tool contract, inspect the focused discovery/router tests and
regenerate the golden only after confirming the schema change is intentional.
For a live SDK change, run the smallest available live smoke separately and
retain its structured summary and log path.

## Issue context

Read issue input with explicit JSON and validate the result before reasoning
from it:

```powershell
pwsh -NoProfile -File scripts/read-issue.ps1 -Issue <number>
```

The helper rejects an empty response, invalid JSON, a mismatched issue number,
or missing title/state. Never treat a successful `gh` exit code alone as proof
that issue context was retrieved.
