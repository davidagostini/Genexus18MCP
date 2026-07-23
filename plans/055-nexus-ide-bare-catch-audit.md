# Plan 055: Audit bare catches — stop swallowing real failures (Phase 2)

> **Executor instructions**: Follow this plan step by step. Verify each. STOP-and-report on
> STOP conditions. Update `plans/README.md` when done. Keep narration minimal; final report in one message.
>
> **Drift check (run first)**: `git diff --stat f98ecd0..HEAD -- src/nexus-ide/src`

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: LOW-MED (touches error paths; must not change intended best-effort behavior)
- **Depends on**: 054 (Logger — surfaced failures log through it)
- **Category**: robustness / bug
- **Planned at**: commit `f98ecd0`, 2026-07-23
- **Roadmap**: `docs/nexus-ide-roadmap.md` Phase 2

## Why this matters

The extension has **~31 bare `catch {}` blocks** (no binding, no logging) in non-test code.
Some are deliberate best-effort (lease cleanup, optional pings); others silently swallow real
read/parse failures, so when something breaks, there's no trace — the #1 reason "it just
doesn't work" is unsupportable. This plan classifies each and makes the non-deliberate ones
observable (log via the Logger from plan 054), WITHOUT changing intended control flow.

## Current state

- `grep -rn "catch\s*{" src/nexus-ide/src --include=*.ts | grep -v /test/` → ~31 sites.
  Known clusters from recon: `completionProvider` (×2), `extension` (×3), `gxFileSystem` (×2),
  `gxShadowService`, `diagnosticProvider`, `managers/BackendManager` (×2), and others.
- Two categories to distinguish:
  - **Deliberate best-effort** — the operation is genuinely optional and failure is expected
    (e.g. a health-check ping, lease-file cleanup, an appendLifecycleLog). These stay
    swallowed, but get a one-line `Logger.debug` so a trace exists.
  - **Real-failure swallow** — a read/parse/IO whose failure changes behavior (e.g.
    `gxFileSystem.ts:139` best-effort ping ignored, a JSON.parse of KB data). These get
    `Logger.warn`/`error` with context, and the catch is bound (`catch (e)`).
- `Logger` (from plan 054) is the sink. `src/nexus-ide/src/test/**` + `npm run check` gate.

## Commands you will need

Run from `src/nexus-ide`:

| Purpose | Command | Expected |
|---------|---------|----------|
| Compile | `npm run compile` | exit 0 |
| Gate | `npm run check` | compile+lint+test green |

## Scope

**In scope**:
- The non-test `.ts` files containing bare `catch {}` (edit the catch blocks only).
- `src/nexus-ide/src/test/**` (a test or two for the highest-value now-surfaced failure).

**Out of scope**:
- Changing the SUCCESS path or the intended outcome of any operation — this is about making
  failures observable, not reworking logic.
- Refactoring the functions containing the catches beyond the catch block itself.
- Introducing new user-facing error dialogs for best-effort operations (log, don't nag).

## Steps

### Step 1: Classify every bare catch

Enumerate the ~31 sites. For each, decide deliberate-best-effort vs real-failure-swallow,
recording the classification (a short table in the PR/notes). When unsure, treat as
real-failure (safer to log than to hide).

**Verify**: a classification list covering all sites.

### Step 2: Make them observable

- Deliberate best-effort: `catch { }` → `catch (e) { Logger.debug("<context>", e); }` (or leave
  truly-hot no-ops with a comment if a debug log would spam a tight loop — justify each).
- Real-failure: `catch { }` → `catch (e) { Logger.warn/error("<what failed + why it matters>", e); }`
  and, where the swallow currently hides a state the caller should know about, surface it
  appropriately (return a sentinel / rethrow ONLY if the caller already handles it — do NOT
  introduce new unhandled throws). Keep control flow otherwise identical.

**Verify**: `grep -rn "catch\s*{[^A-Za-z]*}" src/nexus-ide/src --include=*.ts | grep -v /test/`
→ 0 truly-empty catches (or a justified allowlist for hot-loop no-ops). `npm run compile` exit 0.

### Step 3: Test the highest-value surface

Add at least one test proving a previously-swallowed real failure now logs (inject a failing
dependency + a fake Logger sink, assert it recorded). Don't test every site — pick the most
impactful (e.g. a KB read/parse failure path).

**Verify**: `npm run check` → all green (state counts).

## Done criteria

- [ ] Every bare `catch {}` is either logging (bound + Logger call) or a justified, commented hot-loop no-op.
- [ ] No control-flow/success-path change (diff is catch-block edits only).
- [ ] At least one test proves a real failure now surfaces via the Logger.
- [ ] `npm run check` green; no files outside scope; `plans/README.md` updated.

## STOP conditions

- 054 (Logger) isn't merged — this plan logs through it; land 054 first.
- A catch turns out to be load-bearing control flow (the code RELIES on the swallow to
  continue) — keep the behavior, add only a debug log, and note it.
- Surfacing a failure would require rearchitecting the caller — log it and record the
  rearchitecture as a follow-up rather than doing it here.

## Maintenance notes

- Reviewer: verify no success path changed and no NEW unhandled exception was introduced —
  the goal is visibility, not behavior change.
- After this, "it silently does nothing" bug reports become diagnosable from the Logger output.
