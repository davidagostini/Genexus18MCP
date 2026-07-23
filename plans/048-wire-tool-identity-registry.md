# Plan 048: Wire the tool-identity registry into the catalogs + guard test

> **Executor instructions**: Follow this plan step by step. Run every verification
> command and confirm the expected result before moving on. If a STOP condition
> occurs, stop and report. Update the status row in `plans/README.md` when done.
>
> **Drift check (run first)**: `git diff --stat cf736ec..HEAD -- src/GxMcp.Gateway/ToolIdentity.cs src/GxMcp.Gateway/NextLegalActionsBuilder.cs src/GxMcp.Gateway/ToolHelpCatalog.cs docs/tool-identity-registry.md`

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: LOW-MED
- **Depends on**: 046 (the `ToolIdentity` prototype + design doc — already DONE)
- **Category**: tech-debt / agent-ergo
- **Planned at**: commit `cf736ec`, 2026-07-23

## Why this matters

Plans 041 and 044 fixed the same bug twice: curated catalogs
(`NextLegalActionsBuilder`, `ToolHelpCatalog`) were keyed on pre-consolidation tool
names and silently broke when the umbrella consolidation renamed tools. Plan 046
(DONE) built and unit-tested a non-wired `ToolIdentity` registry (canonical names,
alias→canonical resolution, actions-per-tool) and wrote `docs/tool-identity-registry.md`
with a migration proposal. This plan **executes that migration**: route the catalogs
(and `DidYouMean`'s tool-name candidates) through `ToolIdentity`, and add a guard test
that fails when any catalog references a tool name `ToolIdentity` doesn't know — so
the drift class cannot recur.

## Current state

- `src/GxMcp.Gateway/ToolIdentity.cs` — the registry from plan 046 (NOT wired into
  anything yet). Read it + its tests (`ToolIdentityTests.cs`) first to learn the API
  (`CanonicalToolNames`, `ResolveCanonical`, `ActionsFor`, `IsKnownTool`, `IsRemoved`).
- `docs/tool-identity-registry.md` — **read §2 (API) and §4 (migration proposal +
  guard-test proposal) in full; they are the spec for this plan.**
- `src/GxMcp.Gateway/NextLegalActionsBuilder.cs` — switch on tool name (plan 041 added
  a canonical `genexus_create` case; legacy cases remain as fallthroughs).
- `src/GxMcp.Gateway/ToolHelpCatalog.cs` — `_helpTexts` dict + `Get` (plan 044 made it
  alias-aware via `TryRewriteLegacyTool`).
- `src/GxMcp.Gateway/DidYouMean.cs` — `Suggest`/`FormatSuggestionMessage`; callers pass
  candidate lists (e.g. `ObjectRouter.cs:25,42`). No central tool-name candidate source.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | `0 Erro(s)` |
| Gateway tests | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj -v:minimal` | all pass |

## Scope

**In scope**:
- `src/GxMcp.Gateway/ToolIdentity.cs` (extend if the migration needs a query it lacks)
- `src/GxMcp.Gateway/NextLegalActionsBuilder.cs`, `ToolHelpCatalog.cs` (route through `ToolIdentity`)
- `src/GxMcp.Gateway/DidYouMean.cs` callers that need the canonical tool list (only if the doc's proposal covers it)
- `src/GxMcp.Gateway.Tests/` — the guard test + updated catalog tests

**Out of scope**:
- `McpRouter.TryRewriteLegacyTool` (the alias map stays the source `ToolIdentity` consults)
- `tool_definitions.json` content
- Adding NEW help/next_legal_actions coverage — that is plan 049 (this plan only
  migrates existing entries onto the registry so 049 can build on it)

## Steps

### Step 1: Follow the migration proposal in the 046 doc

Implement `docs/tool-identity-registry.md` §4 step by step: re-express
`NextLegalActionsBuilder`'s dispatch and `ToolHelpCatalog`'s keys in terms of
`ToolIdentity` canonical names, and feed `DidYouMean` the canonical tool list where a
caller suggests tool names. Keep behavior identical to today for every currently-working
name (041 and 044 already made these correct — this step removes the duplicated
hardcoding, not the behavior).

**Verify**: `dotnet build ...` → `0 Erro(s)`.

### Step 2: Add the guard test

Add the guard test the doc proposes: iterate every tool name referenced by
`NextLegalActionsBuilder` and `ToolHelpCatalog` (their switch cases / dict keys) and
assert each is `ToolIdentity.IsKnownTool(name)` (canonical or alias). This is the test
that would have caught 041 and 044 automatically. If enumerating a switch's cases isn't
reflectable, assert instead over the set of tool names each catalog can EMIT/serve
(e.g. every `Suggest(tool, …)` target and every `_helpTexts` key).

**Verify**: the guard test passes now; then temporarily add a bogus `genexus_nonexistent`
key to `_helpTexts`, confirm the guard test FAILS, then remove it (prove the guard bites).

### Step 3: Full-suite regression gate

**Verify**: `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj -v:minimal` → all pass.

## Done criteria

- [ ] `dotnet build` exits 0.
- [ ] `NextLegalActionsBuilder` and `ToolHelpCatalog` resolve tool identity through
      `ToolIdentity` (no independent hardcoded canonical/legacy maps remain — grep for
      leftover legacy literals).
- [ ] Guard test exists, passes, and demonstrably fails on an injected unknown tool name.
- [ ] Full gateway suite passes.
- [ ] `plans/README.md` status row updated.

## STOP conditions

- The 046 doc's migration proposal turns out infeasible against the real catalog shapes
  (e.g. a switch can't be driven from data without a bigger refactor) — report; a
  partial migration (guard test only) may still be worth landing.
- Routing DidYouMean through the registry changes an existing suggestion message a test
  asserts on — reconcile per the doc's intent, report if ambiguous.

## Maintenance notes

- After this, plan 049 (catalog coverage) should ADD entries using `ToolIdentity` as the
  authoritative tool list, and the guard test keeps new entries honest.
- Reviewer: the win is "one source of truth" — confirm no catalog still carries its own
  canonical/legacy name map.
