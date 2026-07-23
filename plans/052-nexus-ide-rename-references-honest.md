# Plan 052: Honest rename + real reference/definition locations (Phase 1)

> **Executor instructions**: Follow this plan step by step. Run every verification
> command and confirm the expected result before moving on. If a STOP condition occurs,
> stop and report. Update the status row in `plans/README.md` when done.
>
> **Drift check (run first)**: `git diff --stat 52e66f1..HEAD -- src/nexus-ide/src/renameProvider.ts src/nexus-ide/src/referenceProvider.ts src/nexus-ide/src/definitionProvider.ts`

## Status

- **Priority**: P1
- **Effort**: M
- **Risk**: MED (changes user-facing editor behavior)
- **Depends on**: 051 (test baseline — land the safety net first)
- **Category**: bug / agent-ergo (human-facing)
- **Planned at**: commit `52e66f1`, 2026-07-23
- **Roadmap**: `docs/nexus-ide-roadmap.md` Phase 1

## Why this matters

Three language features look like they work but don't, and real users hit them:
- **Rename** runs the server-side refactor but returns an **empty** `WorkspaceEdit`
  (`renameProvider.ts:74`), so VS Code's rename UI shows no change — the user can't tell
  it worked and the editor stays stale.
- **References** returns every hit at `Position(0,0)` (`referenceProvider.ts:29`) — "which
  objects reference this" with no real location — and refuses variable references (`:19`).
- **Definition** for a remote object also returns `(0,0)` (`definitionProvider.ts:55`) —
  acceptable (it opens the right object) but worth improving if cheap.

## Current state

- `src/nexus-ide/src/renameProvider.ts` — `provideRenameEdits` calls
  `provider.refactor({action:'RenameVariable'|'RenameAttribute', ...})`, notifies, then
  `return new vscode.WorkspaceEdit();` (line 74) with the comment "we don't have a full
  multi-part editor sync yet." It also fires `nexus-ide.refreshDiagnostics` (line 71).
- `src/nexus-ide/src/referenceProvider.ts` — `provideReferences` does
  `provider.queryObjects('usedby:'+targetName, ...)` and maps each result to
  `new vscode.Location(GxUriParser.toEditorUri(obj.type, obj.name), new vscode.Position(0,0))`
  (lines 26-31). Strips `&` and skips variable search (line 19-20).
- `src/nexus-ide/src/definitionProvider.ts` — local `do <Sub>` → real position (lines
  31-41, good); remote object → `Location(toEditorUri(...), Position(0,0))` (line 53-55).
- The reload mechanism to mirror already exists: `GxShadowService.hydrateOpenedFile(uri, provider)`
  + `provider.fireFileChange(uri)` (used by `SyncManager.handleUpdateNotification`,
  `managers/SyncManager.ts:125-128`) — reuse this to make a rename visible.
- `GxFileSystemProvider.queryObjects(query, limit, timeoutMs)` and a source-read path
  exist (the shadow service reads object source). Confirm the exact read call by grepping:
  `grep -rn "readObjectSource\|part.*Source\|readObjectVariables" src/nexus-ide/src`.

## Commands you will need

Run from `src/nexus-ide/`:

| Purpose | Command | Expected |
|---------|---------|----------|
| Compile | `npm run compile` | exit 0 |
| Lint | `npm run lint` | exit 0 (baseline) |
| Test | `npm test` | all pass |

## Scope

**In scope**:
- `src/nexus-ide/src/renameProvider.ts`, `referenceProvider.ts`, `definitionProvider.ts`
- `src/nexus-ide/src/test/**` (add characterization + new-behavior tests)

**Out of scope**:
- `GxShadowService` / `GxFileSystemProvider` internals (reuse their public methods; don't rewrite them)
- `genexus_refactor` server behavior
- Rename of anything other than variables/attributes (objects/subs stay as-is)

## Steps

### Step 1: Make rename VISIBLE after the server refactor

Keep the server-side refactor call. Replace the "return empty edit + hope" ending with a
real post-rename refresh so the user sees the change: after a successful `refactor`,
re-hydrate the affected open editor(s) via `GxShadowService.hydrateOpenedFile` +
`provider.fireFileChange` (the same mechanism `SyncManager` uses), then keep the success
notification. Continue returning `undefined`/empty so VS Code does NOT attempt a naive
local text replace — but the editor content now actually updates. If the refactor touched
other open parts/objects, refresh those too (match by object name like
`SyncManager.handleUpdateNotification` does).

**Verify**: `npm run compile` exit 0. Add a test (with a stubbed `provider.refactor`
resolving success and a spy on the hydrate/refresh path) asserting the refresh is invoked
on success and NOT on error.

### Step 2: Real reference locations

In `referenceProvider.ts`, for each object returned by `usedby:`, fetch that object's
source (the shadow/read path from Step "Current state") and locate the actual line(s)
where `targetName` appears, emitting a `Location` per real occurrence with a correct
`Range`. Bound the work: cap the number of objects deep-scanned (e.g. first 50) and log
(via the extension's output, not a silent drop) when truncated. If source fetch fails for
an object, fall back to the object-level `(0,0)` location for that one (don't lose it).
Also support **variable references** within the current object at minimum (drop the `&`,
scan the open document's parts) — full cross-KB variable search can stay a documented follow-up.

**Verify**: `npm run compile` exit 0. Test with a stubbed `queryObjects` returning 2
objects + stubbed source containing the token at known lines → assert real ranges, and
the truncation cap behavior.

### Step 3: Definition (cheap improvement only)

Leave the local-`Sub` path as-is. For the remote object, `(0,0)` is acceptable (opens the
right object). Only if the source-locate helper from Step 2 is trivially reusable, point
the definition at the token's defining line; otherwise leave `(0,0)` and note it. Do NOT
over-invest here.

**Verify**: `npm run compile` exit 0; existing definition test (from 051) still green.

### Step 4: Gate

**Verify**: `npm run compile && npm run lint && npm test` (or the 051 `check` gate) → all green.

## Test plan

- Rename: success → refresh invoked; error → not invoked, error surfaced.
- References: real ranges from stubbed source; truncation cap; per-object fallback on
  source-fetch failure; within-object variable reference.
- Definition: local sub unchanged; remote still resolves the right object.
- Pattern: `src/nexus-ide/src/test/suite/extension.test.ts` + whatever 051 added.

## Done criteria

- [ ] `npm run compile` exits 0; `npm test` all pass with new tests.
- [ ] Rename updates the open editor content on success (no more silent empty edit) — test proves the refresh path fires.
- [ ] References emit real `Range`s for at least the deep-scanned objects (test proves it), with a documented cap and per-object fallback.
- [ ] Within-object variable references return something (no longer unconditionally skipped).
- [ ] No files outside scope modified; `plans/README.md` status row updated.

## STOP conditions

- The excerpts at the cited lines don't match live code (drift).
- There's no public shadow/read method to fetch an object's source without a large
  refactor — then references' real-range upgrade isn't cheaply feasible; ship Step 1
  (rename visibility) + variable-within-object refs, and record the cross-object real-range
  work as a follow-up rather than refactoring the shadow service here.
- Re-hydrating on rename fights a dirty open document — mirror `SyncManager`'s dirty-doc
  guard (`SyncManager.ts:119-122`): skip refresh for dirty docs and tell the user to reload.

## Maintenance notes

- Reuse `SyncManager`'s hydrate+fireFileChange pattern for the rename refresh — if plan 053
  wires `SyncManager`, keep the two consistent (ideally share a helper).
- Reviewer: the bar is "the user SEES the rename and lands on real reference lines." A test
  that only checks the server call fired is insufficient — assert the editor-refresh / range output.
