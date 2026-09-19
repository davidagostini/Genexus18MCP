# Plan 103: Split OperationsRouter into typed route modules

> Executor: follow this plan step by step. Do not modify files outside Scope. Run every verification gate. If a STOP condition occurs, stop and report instead of improvising. Before starting, run `git diff --stat d77c20f..HEAD -- <in-scope paths>` and reconcile any drift. Do not commit or push unless explicitly authorized.

**Planned at:** commit `d77c20f`.


## Status

- Priority: P2
- Category: architecture
- Effort: L
- Risk: HIGH
- Depends on: none unless noted below

## Why this matters

This is a vetted improvement from the 2026-09-09 audit. It has concrete evidence in the current repository and a bounded verification path.

## Current state and implementation

`src/GxMcp.Gateway/Routers/OperationsRouter.cs:10-538` is the central switch in a 1,442-line file with high fan-in; existing plan `plans/005-command-dispatch-table.md` records related deferred debt.

Add domain route modules behind a registry while preserving one normalization boundary, action aliases, null/default semantics, and published schema. Move one domain at a time with characterization tests and contract/golden checks.

Scope: OperationsRouter, new route modules, tests, registrations. Out of scope: public tool renames or response changes.

Done: all routes have equivalent dispatch tests, router complexity is reduced, and discovery/golden fixtures remain identical.

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
