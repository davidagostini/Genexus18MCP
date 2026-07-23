# Plan 059: Diagnostics-driven code actions (Phase 3)

> **Executor instructions**: Follow step by step, verify each, STOP-and-report on STOP
> conditions, update `plans/README.md` when done. Minimal narration; final report in one message.
>
> **Drift check (run first)**: `git diff --stat f9dd16c..HEAD -- src/nexus-ide/src/codeActionProvider.ts src/nexus-ide/src/diagnosticProvider.ts`

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: LOW
- **Depends on**: 051 (test baseline)
- **Category**: feature
- **Planned at**: commit `f9dd16c`, 2026-07-23
- **Roadmap**: `docs/nexus-ide-roadmap.md` Phase 3

## Why this matters

`codeActionProvider.ts` offers ONE hardcoded quick-fix ("Create Variable") for ANY `&word`
under the cursor — with an in-code admission it doesn't check whether the variable actually
needs creating (lines 20-21). Meanwhile `diagnosticProvider.ts` already produces rich,
structured lint diagnostics (via `genexus_analyze` linter — code/severity/message/range). This
plan drives code actions off those real diagnostics: offer a fix only when a diagnostic at the
cursor calls for it, mapped to the diagnostic's code — so quick-fixes are relevant, not blanket.

## Current state

- `src/nexus-ide/src/codeActionProvider.ts` — the whole file (~33 lines): ignores
  `_context.diagnostics`, offers "Create Variable &X" for any `&word` (lines 16-28).
- `src/nexus-ide/src/diagnosticProvider.ts` — `GxDiagnosticProvider` maps `genexus_analyze`
  linter issues to `vscode.Diagnostic`s (with `code`/`severity`/range). Read it to learn the
  diagnostic `code` values it sets (e.g. an undeclared-variable code) — those codes are the
  keys the code-action provider should switch on.
- `vscode.CodeActionContext.diagnostics` gives the diagnostics overlapping the range — use it.
- `Logger`, `check` gate, `@vscode/test-electron` (baseline 62 tests).

## Commands you will need

Run from `src/nexus-ide`: `npm run compile`, `npm run lint`, `npm run check`.

## Scope

**In scope**:
- `src/nexus-ide/src/codeActionProvider.ts`
- `src/nexus-ide/src/test/**`
- `src/nexus-ide/src/diagnosticProvider.ts` ONLY if a diagnostic needs a stable `code` set for
  the action provider to key on (minimal — add/confirm codes, don't change detection).

**Out of scope**:
- Adding new lint rules / changing what diagnostics are produced.
- Auto-applying fixes without the user invoking them.

## Steps

### Step 1: Map diagnostics → actions

Read `_context.diagnostics`. For each diagnostic overlapping the cursor whose `code` maps to a
known fix, offer the corresponding `CodeAction` (`QuickFix`, `.diagnostics = [thatDiagnostic]`,
`.isPreferred` where obvious). Start with the clear ones the linter emits — e.g. an
undeclared/unknown variable diagnostic → "Create Variable &X" bound to `nexus-ide.createVariable`
with the REAL name from the diagnostic (not any `&word`). Enumerate the diagnostic codes from
`diagnosticProvider.ts` and cover the ones with an obvious, safe fix.

**Verify**: `npm run compile` exit 0. Test: a document + a stubbed undeclared-variable diagnostic
at the cursor → the Create-Variable action is offered and carries the right name; NO diagnostic →
no action offered (the key behavior change).

### Step 2: Remove the blanket `&word` fallback

Drop the "offer Create Variable for any `&word`" behavior — actions come from diagnostics now.
(If you keep any non-diagnostic action, justify it.)

**Verify**: `npm run check` → all green (state counts).

## Done criteria

- [ ] Code actions are driven by `_context.diagnostics` (test: action offered only when a matching diagnostic is present at the cursor).
- [ ] The Create-Variable action uses the diagnostic's real target, `.diagnostics` set, correct `kind`.
- [ ] No blanket "any `&word`" action remains.
- [ ] `npm run check` green; diagnostics detection unchanged; `plans/README.md` updated.

## STOP conditions

- Drift on the two files.
- `diagnosticProvider` doesn't set stable `code`s to key on and adding them is more than a small
  change — ship keyed off diagnostic MESSAGE matching as a fallback and note the code-stability follow-up.
- A diagnostic's "obvious fix" isn't actually safe/obvious — offer only the clearly-correct ones, list the rest as follow-up.

## Maintenance notes

- Reviewer: confirm actions no longer appear on arbitrary `&words` (only on real diagnostics) and
  each action attaches the diagnostic it resolves.
- New lint rules that want a quick-fix should add their `code`→action mapping here.
