# Plan 046: Design a single tool-identity registry (spike)

> **Executor instructions**: This is a DESIGN / SPIKE plan, not a build-everything
> plan. The deliverable is a short design doc + a minimal prototype + a list of open
> questions — NOT a finished feature. Follow the steps, and if a step reveals the
> approach is wrong, STOP and report your finding rather than forcing it. When done,
> update the status row in `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 4082fd3..HEAD -- src/GxMcp.Gateway/NextLegalActionsBuilder.cs src/GxMcp.Gateway/ToolHelpCatalog.cs src/GxMcp.Gateway/DidYouMean.cs src/GxMcp.Gateway/McpRouter.cs`

## Status

- **Priority**: P3
- **Effort**: M (spike) — full implementation is a separate follow-up
- **Risk**: LOW (spike writes a doc + prototype, not production behavior)
- **Depends on**: informed by 041 and 044 (the two bugs this would prevent)
- **Category**: direction
- **Planned at**: commit `4082fd3`, 2026-07-23

## Why this matters

Plans 041 (next_legal_actions dead for the create family) and 044 (tool-help keyed by
legacy names) are the **same root cause**: the umbrella-tool consolidation renamed
tools (e.g. `genexus_create_object` → `genexus_create action=object`), but several
curated, hand-maintained catalogs still key off the pre-consolidation names, and
nothing forces them to stay in sync. Each catalog drifts independently and fails
silently. A single source of truth for tool identity — canonical name, its actions,
and its legacy aliases — that `NextLegalActionsBuilder`, `ToolHelpCatalog`, and
`DidYouMean` all consult would make the next consolidation a one-place change and let
a test assert every catalog covers exactly the live tool set. This spike decides
whether that registry is worth building and what its API should be.

## Current state (the drift surface)

Three catalogs independently hard-code tool names, at least two already stale:

- `src/GxMcp.Gateway/NextLegalActionsBuilder.cs:53-63` — `switch` on tool names;
  cases `genexus_create_object`/`genexus_create_popup`/`genexus_save_as` are dead
  after consolidation (see plan 041).
- `src/GxMcp.Gateway/ToolHelpCatalog.cs:9-254` — `_helpTexts` keyed by tool name;
  `genexus_create_object`, `genexus_db_optimize` are legacy keys (see plan 044).
- `src/GxMcp.Gateway/DidYouMean.cs` — suggests names from candidate lists passed by
  callers (e.g. `ObjectRouter.cs:25,42` pass `_validEditModes`/`_validSemanticOps`);
  there is no central list of valid tool names it draws from.
- The authoritative data already exists in two places: `tool_definitions.json`
  (canonical tool names + their `action` enums + input schemas) and
  `McpRouter.TryRewriteLegacyTool` (`:1120-~1420`, the legacy→canonical+action map).
- `GatewayArgsValidator` already loads and caches `tool_definitions.json`
  (`GatewayArgsValidator.cs:177-215`) — a proven pattern for reading it at runtime.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | `0 Erro(s)` |
| Gateway tests | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj -v:minimal` | all pass |

## Scope

**In scope (spike deliverables)**:
- `docs/tool-identity-registry.md` — the design doc (create).
- Optionally a minimal, NON-wired prototype type `src/GxMcp.Gateway/ToolIdentity.cs`
  that exposes the proposed API against real data, plus a unit test proving it can
  answer the key questions — but DO NOT rewire `NextLegalActionsBuilder` /
  `ToolHelpCatalog` / `DidYouMean` to use it in this plan.

**Out of scope**:
- Actually migrating the three catalogs onto the registry (that's the follow-up plan
  the doc will propose).
- Changing `tool_definitions.json` or `TryRewriteLegacyTool`.

## Steps

### Step 1: Inventory the drift

Enumerate, in the doc, every place a tool name is hard-coded outside
`tool_definitions.json` and `TryRewriteLegacyTool`: the two catalogs above, the
DidYouMean candidate lists, and any others found via
`grep -rn '"genexus_' src/GxMcp.Gateway --include=*.cs | grep -v Tests`. For each,
note whether it's keyed by canonical or legacy names today.

**Verify**: doc lists ≥3 catalogs with their current keying.

### Step 2: Define the registry API

Propose a small static/singleton `ToolIdentity` with (at least):
- `IReadOnlyList<string> CanonicalToolNames` — sourced from `tool_definitions.json`.
- `string ResolveCanonical(string nameOrAlias)` — legacy alias → canonical (reuse
  `TryRewriteLegacyTool`).
- `IReadOnlyList<string> ActionsFor(string canonicalTool)` — the `action` enum from
  the tool's input schema.
- `bool IsKnownTool(string name)` — canonical or alias.

Specify how each is derived from the two existing authoritative sources so the
registry has NO independently-maintained tool list of its own. State the caching
strategy (mirror `GatewayArgsValidator`'s load-once cache).

**Verify**: doc has a C# interface/signature sketch + a data-source column per member.

### Step 3: Prototype + prove the key queries (optional but recommended)

Implement `ToolIdentity` against the real `tool_definitions.json` + `TryRewriteLegacyTool`
and write a unit test asserting:
- `ResolveCanonical("genexus_create_object") == "genexus_create"`.
- `ActionsFor("genexus_create")` contains `object`, `popup`, `save_as`.
- `IsKnownTool("genexus_create")` and `IsKnownTool("genexus_create_object")` are both true.
Do NOT wire it into the three catalogs.

**Verify**: `dotnet test ... ` → the new `ToolIdentity` test passes; full gateway suite still green.

### Step 4: Propose the migration + the guard test

In the doc, outline the follow-up plan: re-express `NextLegalActionsBuilder`'s switch
and `ToolHelpCatalog`'s keys in terms of `ToolIdentity` canonical names, feed
DidYouMean the canonical tool list, and add a **guard test** that fails when a catalog
references a name that `ToolIdentity.IsKnownTool` rejects (this is what would have
caught 041 and 044 automatically). List open questions: per-action help vs per-tool
help granularity; whether next_legal_actions should be data-driven per action; cost
of loading schemas at gateway start.

**Verify**: doc ends with a concrete follow-up plan sketch + ≥3 open questions.

## Done criteria

- [ ] `docs/tool-identity-registry.md` exists with: drift inventory, API sketch with
      data sources, migration proposal, guard-test proposal, open questions.
- [ ] If prototyped: `dotnet build` exits 0, `ToolIdentity` unit test passes, full
      gateway suite green, and NO existing catalog was rewired.
- [ ] `plans/README.md` status row updated.

## STOP conditions

- The authoritative sources can't actually answer the queries (e.g.
  `tool_definitions.json` doesn't expose `action` enums in a parseable way, or
  `TryRewriteLegacyTool` isn't reusable read-only) — report this; it changes whether
  the registry is feasible at all.
- The drift inventory turns up a fourth+ independently-maintained tool list that
  materially changes the design — report before prototyping.

## Maintenance notes

- This spike deliberately does not change runtime behavior. The tactical fixes (041,
  044) can and should land independently first; the registry then subsumes them.
- Reviewer: judge the doc on whether a follow-up executor could build the registry +
  migrate the catalogs from it alone, and whether the proposed guard test would
  actually have caught 041/044.
