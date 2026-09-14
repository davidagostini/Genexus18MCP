# Agent coordination

Use this guide when delegating implementation lanes or integrating patches from
more than one agent. Keep each lane independently reviewable and leave shared
artifacts to the coordinator.

## Parallel lane contract

- Give every lane an explicit, non-overlapping path allowlist.
- The coordinator owns `CHANGELOG.md`, generated fixtures/goldens, release
  metadata, lockfiles, and client-registration artifacts. Agents must report
  required changes to those files instead of editing them in parallel.
- An agent may commit its isolated functional patch for transport, but the
  coordinator reviews and integrates it; no lane publishes, pushes, merges, or
  closes an issue.
- Preserve unrelated staged and worktree changes when integrating a lane.

## Required handoff

Every lane returns this small record, even when it made no change:

```text
status: fixed | no_change | blocked
lastEditAt: <ISO-8601 timestamp or none>
testsAfterLastEdit: <commands and result, or none>
unrunGates: <commands still required>
changedContracts: <schema/router/fixture/API contracts or none>
generatedArtifactsNeeded: <files or none>
changelogEntry: <proposed Unreleased entry or none>
```

`testsAfterLastEdit` is authoritative: a test run from before the final edit
cannot be reported as final validation.

## Coordinator gate

After all lanes are integrated, run from the repository root:

```powershell
npm run test:integration
```

This validates the combined commit/index/worktree diff, rejects conflict
markers and incomplete discovery-contract changes, runs the existing
PowerShell/Node gates, and executes the .NET solution serially with an explicit
timeout. Its JSON summary and phase logs are written outside the repository;
keep the printed path as evidence. Use `-ValidateOnly` only for checking the
file/contract policy without executing tests.

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
