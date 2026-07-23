# Plan 058: Real, context-aware inline completion (Phase 3)

> **Executor instructions**: Follow step by step, verify each, STOP-and-report on STOP
> conditions, update `plans/README.md` when done. Minimal narration; final report in one message.
>
> **Drift check (run first)**: `git diff --stat f9dd16c..HEAD -- src/nexus-ide/src/inlineCompletionProvider.ts src/nexus-ide/src/completionProvider.ts`

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: LOW-MED (new provider behavior; must degrade gracefully)
- **Depends on**: 051 (test baseline)
- **Category**: feature
- **Planned at**: commit `f9dd16c`, 2026-07-23
- **Roadmap**: `docs/nexus-ide-roadmap.md` Phase 3

## Why this matters

`inlineCompletionProvider.ts` is a placeholder: 4 hardcoded regex branches emitting fixed
strings (`&var.` → literal `"IsEmpty()"`/`"SetEmpty()"`/`"Count"`; bare `if`/`for each` → fixed
snippets). It ignores the real variable type, the KB, and the MCP entirely — so its ghost text
is usually wrong. This plan makes it context-aware: real member suggestions from the SDK for
`&var.` (reusing the resolution the completion provider already does), and optional AI-backed
free-form completion via `genexus_ai_complete` when that endpoint is configured — degrading
cleanly to nothing (never wrong ghost text) otherwise.

## Current state

- `src/nexus-ide/src/inlineCompletionProvider.ts` — the whole file (~38 lines): `provideInlineCompletionItems`
  with the 4 hardcoded branches (lines 15-34).
- `src/nexus-ide/src/completionProvider.ts` — ALREADY resolves `&var.` member access to real
  members via `genexus_structure`/type resolution (part-aware). Read it to find the reusable
  resolution (e.g. a method that, given a variable name + document, returns its members/type).
  Reuse that logic — do NOT reimplement type resolution.
- `genexus_ai_complete` MCP tool: forwards a prompt to an OpenAI-compatible endpoint (env
  `GXMCP_AI_COMPLETE_URL`/`_KEY`/`_MODEL`); returns `{completion,...}` or
  `{code:"AiEndpointNotConfigured"}` when unset. Call it via `provider.callMcpTool("genexus_ai_complete", {context})`.
- `Logger` (`src/utils/Logger.ts`), `check` gate, `@vscode/test-electron` (baseline 62 tests).

## Commands you will need

Run from `src/nexus-ide`: `npm run compile`, `npm run lint`, `npm run check` (compile+lint+test).

## Scope

**In scope**:
- `src/nexus-ide/src/inlineCompletionProvider.ts`
- A small shared helper if you extract member-resolution from `completionProvider.ts` (keep the
  completion provider working — extract, don't fork).
- `src/nexus-ide/src/test/**`.

**Out of scope**:
- `completionProvider.ts` behavior (reuse its resolution; don't change what IT returns).
- Building a bespoke ranking model; multi-line AI code generation.
- Making `genexus_ai_complete` mandatory (must degrade when unconfigured).

## Steps

### Step 1: Real member ghost text for `&var.`

Replace the hardcoded `&var.` branch: resolve the variable's real members/type (reuse the
completion provider's resolution) and emit those as inline items. If resolution yields nothing
(unknown variable), emit NOTHING (no fixed guesses). Keep it fast + cancellation-aware
(`_token`); debounce/skip if the token is cancelled.

**Verify**: `npm run compile` exit 0. Test with a stubbed resolver returning known members →
asserts those become inline items; unknown var → empty.

### Step 2: Optional AI free-form completion (graceful)

When the line isn't a `&var.` member case, optionally call `genexus_ai_complete` with the
surrounding code as context (debounced, cancellation-aware, short timeout). If it returns a
completion, emit it as ghost text; if it returns `AiEndpointNotConfigured` or errors/times out,
return nothing (never block typing, never show an error). Gate behind a setting
(`genexus.inlineCompletion.ai`, default the safe value) so users opt in.

**Verify**: `npm run compile` exit 0. Test: stubbed `callMcpTool` returning a completion →
ghost text; returning `AiEndpointNotConfigured` → empty, no throw.

### Step 3: Drop the remaining hardcoded guesses

Remove the fixed `if`/`for each` string branches (or replace with real snippet completions if
trivially correct). No literal `IsEmpty()`/`SetEmpty()` strings remain unless they came from
real member resolution.

**Verify**: `npm run check` → all green (state counts).

## Done criteria

- [ ] `&var.` inline completion emits REAL members (from resolution), not hardcoded strings; unknown var → empty (test proves both).
- [ ] AI path degrades cleanly when unconfigured (test proves no throw / empty).
- [ ] No hardcoded `"IsEmpty()"`/`"SetEmpty()"` literal ghost text remains (grep).
- [ ] `npm run check` green; no `completionProvider` behavior change; `plans/README.md` updated.

## STOP conditions

- Drift on the two files.
- The completion provider's member resolution can't be reused without a big refactor — extract
  the minimal helper; if that's too invasive, ship Step 1 with a thinner resolver and note it.
- `genexus_ai_complete` isn't reachable via `provider.callMcpTool` the way expected — ship the
  member-completion (Step 1) which needs no AI endpoint, and record the AI path as follow-up.

## Maintenance notes

- Reviewer: the bar is "never show wrong ghost text." Prefer emitting nothing over a guess.
- The AI path is opt-in + config-dependent; document the env vars (`GXMCP_AI_COMPLETE_*`) in the extension README.
