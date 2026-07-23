# Plan 053: Resolve SyncManager (wire or delete) + fix the mis-wired command (Phase 1)

> **Executor instructions**: Follow this plan step by step. Run every verification
> command and confirm the expected result before moving on. If a STOP condition occurs,
> stop and report. Update the status row in `plans/README.md` when done.
>
> **Drift check (run first)**: `git diff --stat 52e66f1..HEAD -- src/nexus-ide/src/managers/SyncManager.ts src/nexus-ide/src/extension.ts src/nexus-ide/src/gxActionsProvider.ts`

## Status

- **Priority**: P2
- **Effort**: S-M
- **Risk**: MED (wiring SyncManager adds live behavior; deleting removes a feature)
- **Depends on**: 051 (test baseline)
- **Category**: tech-debt / bug
- **Planned at**: commit `52e66f1`, 2026-07-23
- **Roadmap**: `docs/nexus-ide-roadmap.md` Phase 1

## Why this matters

Two concrete defects from the recon:
- **`SyncManager` is dead code**: a complete SSE live-sync subsystem
  (`managers/SyncManager.ts`, 143 lines — SSE listener, cache invalidation, dirty-doc-guarded
  re-hydration, reconnect, a `nexus-ide.syncWithKb` command) is imported at
  `extension.ts:16` but **never instantiated or `register()`ed** (grep: `new SyncManager`
  → zero hits; the other managers all call `.register()` around `extension.ts:643-680`).
  Either it should be delivering live KB→editor sync (a real feature) or it's confusing dead weight.
- **Mis-wired command**: `gxActionsProvider.ts:56` binds the "Explain Code with AI" tree
  item to command `nexus-ide.copyMcpConfig` (copies an MCP config snippet to clipboard) —
  the label promises an AI explanation, the command does something unrelated.

## Current state

- `src/nexus-ide/src/managers/SyncManager.ts` — complete; `register()` (line 17) starts the
  SSE listener and registers `nexus-ide.syncWithKb`. `handleUpdateNotification` (line 94)
  reacts to `notifications/resources/updated`, invalidates cache, and re-hydrates non-dirty
  open docs (dirty-doc guard at lines 119-122). Ctor needs `(context, GxFileSystemProvider, GxShadowService)`.
- `src/nexus-ide/src/extension.ts:16` — `import { SyncManager } ...` (unused). Manager
  registration happens ~`:643-680`: `shadowManager.register()` (`:643`),
  `providerManager.register()` (`:668`), `commandManager.register()` (`:678`),
  `contextManager.register()` (`:680`). The `provider` and a `GxShadowService` instance are
  in scope there (find their locals: `grep -n "new GxShadowService\|shadowService\|const provider" src/nexus-ide/src/extension.ts`).
- `src/nexus-ide/src/gxActionsProvider.ts:56` —
  `new ActionItem('Explain Code with AI', ..., 'nexus-ide.copyMcpConfig')`. The `ActionItem`
  ctor's last arg is the command id (see line 80 `command: commandId`).
- Whether wiring SyncManager is USEFUL depends on the gateway actually emitting
  `notifications/resources/updated` over SSE. Verify in the server:
  `grep -rn "resources/updated\|notifications/resources" src/GxMcp.Gateway`. The gateway
  is known to broadcast `notifications/resources/list_changed` (`Program.Notifications.cs:184`);
  confirm whether it ALSO emits per-resource `updated` events (SyncManager listens for those).

## Commands you will need

Run from `src/nexus-ide/`:

| Purpose | Command | Expected |
|---------|---------|----------|
| Compile | `npm run compile` | exit 0 |
| Lint | `npm run lint` | exit 0 (baseline) |
| Test | `npm test` | all pass |

## Scope

**In scope**:
- `src/nexus-ide/src/extension.ts` (wire OR remove the SyncManager import/usage)
- `src/nexus-ide/src/managers/SyncManager.ts` (delete only if the decision is "delete")
- `src/nexus-ide/src/gxActionsProvider.ts` (fix the command binding)
- `src/nexus-ide/src/test/**` (a test for whichever path)

**Out of scope**:
- The gateway's SSE emission (if it doesn't emit `resources/updated`, changing the SERVER
  is a separate MCP plan — do not touch `src/GxMcp.*` here)
- The broader logging/bare-catch/CSP hygiene (that's roadmap Phase 2, not this plan)

## Steps

### Step 1: Decide SyncManager — wire or delete (evidence-based)

Run the gateway grep above.
- **If the gateway emits `notifications/resources/updated`** (or can be confirmed to over
  the SSE endpoint `SyncManager` connects to): **WIRE it** — instantiate in `extension.ts`
  with `(context, provider, shadowService)` and call `.register()` alongside the other
  managers (~`:668`), and push it to `context.subscriptions` / dispose on deactivate.
- **If it does NOT** emit those events: **DELETE** `SyncManager.ts` and its `extension.ts:16`
  import (dead code that listens for events that never fire). Record in the PR/notes that
  live-sync needs a server-side `resources/updated` emission first (a future MCP plan).

Pick based on the grep result; state which and why.

**Verify**: `npm run compile` exit 0. If wired: a test with a fake SSE `data:` line for
`notifications/resources/updated` asserts `handleUpdateNotification` invalidates cache +
refreshes a non-dirty doc and SKIPS a dirty one. If deleted: `grep -rn "SyncManager" src/nexus-ide/src` → no matches.

### Step 2: Fix the mis-wired command

At `gxActionsProvider.ts:56`, either (a) bind "Explain Code with AI" to the command that
actually explains code if one exists (check `package.json` commands + `CommandManager` for
an AI/explain/`autoFix` command — `nexus-ide.autoFix` is "Auto-Fix Build Errors (AI)"), or
(b) if no explain command exists, RELABEL the item to match what `copyMcpConfig` does
("Copy MCP Config for Copilot/Claude") so label and action agree. Prefer (a) only if a
genuine explain flow exists; otherwise (b) — do not invent a new command in this plan.

**Verify**: `npm run compile` exit 0; the tree item's label and bound command id agree
(assert in a small test, or document the manual check).

### Step 3: Gate

**Verify**: `npm run compile && npm run lint && npm test` (or the 051 `check` gate) → all green.

## Done criteria

- [ ] `npm run compile` exits 0; `npm test` all pass.
- [ ] SyncManager is EITHER wired (instantiated + `register()`ed + disposed, with a test) OR
      fully deleted (no `SyncManager` references remain) — decision recorded with the gateway-emission evidence.
- [ ] `gxActionsProvider.ts:56` label and command id agree (no "Explain Code with AI" → `copyMcpConfig` mismatch).
- [ ] No files outside scope modified; `plans/README.md` status row updated.

## STOP conditions

- The excerpts don't match live code (drift).
- Wiring SyncManager needs a `GxShadowService` / `provider` instance that isn't in scope at
  the registration site without a broader refactor — report; deleting may be the cleaner
  Phase-1 call with wiring deferred.
- The gateway emission situation is ambiguous (emits a differently-named event) — report the
  finding; don't guess-wire a listener to the wrong event name.

## Maintenance notes

- If SyncManager is wired, coordinate with plan 052's rename-refresh (both re-hydrate open
  docs) — ideally share one hydrate helper so behavior is consistent.
- If deleted, note the live-sync feature as a possible future MCP+extension pair (server
  emits `resources/updated`, extension re-adds a listener).
- Reviewer: for the wire path, insist on the dirty-doc-guard test (must not clobber unsaved edits).
