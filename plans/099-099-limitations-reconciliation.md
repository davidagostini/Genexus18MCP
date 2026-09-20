# Plan 099: Reconcile limitations tracking with current evidence

> Executor: follow this plan step by step. Do not modify files outside Scope. Run every verification gate. If a STOP condition occurs, stop and report instead of improvising. Before starting, run `git diff --stat d77c20f..HEAD -- <in-scope paths>` and reconcile any drift. Do not commit or push unless explicitly authorized.

**Planned at:** commit `d77c20f`.


## Status

- Priority: P1
- Category: docs
- Effort: M
- Risk: MED
- Depends on: none unless noted below

## Why this matters

This is a vetted improvement from the 2026-09-09 audit. It has concrete evidence in the current repository and a bounded verification path.

## Current state and implementation

`docs/mcp_limitations_tracking.md:3-5,25,34,67,105,150,171` mixes historical `TODO/IN_PROGRESS/VALIDATING` states with current implementation; compare `docs/mcp_capabilities_inventory.md:102-135`, CI smoke contracts, and `plans/README.md`.

Split historical baseline from current status, attach each item to commit/artifact/evidence, retain genuinely unverified live gates as unverified, and remove duplicate/stale claims.

Scope: limitations tracking and cross-links only. Out of scope: claiming SDK/live behavior without evidence.

Done: every listed capability has one status, provenance, owner/next gate, and no contradiction with inventory.

## Suggested steps

1. Re-read the cited current code and existing neighboring tests; stop if the evidence has drifted.
2. Add characterization/regression tests before changing behavior, using the closest existing test fixture and preserving current public contracts.
3. Implement the smallest change described above, matching the repository's C# xUnit, Node `node:test`, JSON schema, and structured-envelope conventions.
4. Run the narrowest focused test first, then the verification commands below.
5. Review `git diff --stat` and `git diff --check`; ensure only Scope files changed.

## Verification commands

Run from `C:/Projetos/Genexus18MCP`:

- `dotnet test Genexus18MCP.sln --no-restore -v:minimal`
- `npm test`
- `npm run lint`

Expected: exit 0; no new failures. SDK/live tests may remain skipped when the documented GeneXus fixture is unavailable; never report them as passed.

## STOP conditions

Stop and report if cited code no longer matches, if the public MCP response shape must change beyond this plan, if a required SDK/fixture is unavailable, or if verification fails twice.

## Maintenance notes

Keep this plan's evidence and status synchronized with `plans/README.md`. Reviewers should scrutinize backward compatibility, Windows path/process behavior, MCP envelope stability, and whether skipped live/SDK gates are honestly reported.
