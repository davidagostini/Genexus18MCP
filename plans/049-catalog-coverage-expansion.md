# Plan 049: Expand help + next_legal_actions coverage across the tool surface

> **Executor instructions**: Follow this plan step by step. Run every verification
> command and confirm the expected result before moving on. If a STOP condition
> occurs, stop and report. Update the status row in `plans/README.md` when done.
>
> **Drift check (run first)**: `git diff --stat cf736ec..HEAD -- src/GxMcp.Gateway/ToolHelpCatalog.cs src/GxMcp.Gateway/NextLegalActionsBuilder.cs`

## Status

- **Priority**: P3
- **Effort**: M
- **Risk**: LOW
- **Depends on**: 048 (registry + guard test — build coverage on the authoritative tool list)
- **Category**: agent-ergo / docs
- **Planned at**: commit `cf736ec`, 2026-07-23

## Why this matters

Two agent-guidance surfaces cover only a fraction of the tools:
- **`ToolHelpCatalog`** serves per-tool help for ~10 of ~40 tools; the rest return null
  on `resources/read genexus://kb/tool-help/<tool>`.
- **`NextLegalActionsBuilder`** emits follow-up suggestions for only 7 tools
  (apply_pattern, create, edit, lifecycle, save_as→create, versioning). State-changing
  tools with NO follow-ups include `genexus_refactor`, `genexus_rename_across_kb`,
  `genexus_structure`, `genexus_delete_object`, `genexus_transfer`, `genexus_deploy`,
  `genexus_gxserver` (commit/update), `genexus_module` (install).

An agent working those tools gets no "what next" nudge and no on-demand help — it has
to guess. Filling the highest-traffic gaps makes the whole surface as guidable as the
create/edit flow already is.

## Current state

- `src/GxMcp.Gateway/ToolHelpCatalog.cs` — `_helpTexts` dict, keys (post-048) are
  canonical tool names; `Get(name)` resolves aliases. Entries are verbatim markdown
  strings concatenated with `+`. Model new entries on the existing `genexus_query` /
  `genexus_lifecycle` entries (structure: title, actions/prefixes, defaults, examples).
- `src/GxMcp.Gateway/NextLegalActionsBuilder.cs` — `BuildFor` switch; per-tool
  `BuildForX` helpers using the `Suggest(tool, args, why, priority)` helper. Read-only
  tools are in `_readOnlyTools` and correctly emit nothing.
- `src/GxMcp.Gateway/ToolIdentity.cs` (post-048) — authoritative canonical tool list +
  `ActionsFor`. Use `ToolIdentity.CanonicalToolNames` to know what's uncovered.
- The full tool list + schemas: `src/GxMcp.Gateway/tool_definitions.json`.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | `0 Erro(s)` |
| Gateway tests | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj -v:minimal` | all pass |

## Scope

**In scope**:
- `src/GxMcp.Gateway/ToolHelpCatalog.cs` (add entries)
- `src/GxMcp.Gateway/NextLegalActionsBuilder.cs` (add `BuildForX` cases)
- `src/GxMcp.Gateway.Tests/` (extend coverage tests + the 048 guard test naturally covers new names)

**Out of scope**:
- Changing tool behavior or schemas.
- The registry mechanics (that's 048).
- Read-only tools' next_legal_actions (they should stay empty).

## Steps

### Step 1: Rank the gaps by traffic, fill the top ones

Do NOT try to cover all 40 tools in one pass. Using `ToolIdentity.CanonicalToolNames`
minus the currently-covered set, pick the highest-value uncovered **state-changing**
tools for `next_legal_actions` (candidates: `genexus_refactor`, `genexus_rename_across_kb`,
`genexus_structure`, `genexus_delete_object`, `genexus_transfer`, `genexus_deploy`,
`genexus_gxserver` commit/update, `genexus_module` install) and the highest-value
uncovered tools for help (the umbrella tools an agent hits often: `genexus_create`,
`genexus_db`, `genexus_versioning`, `genexus_browser`, `genexus_structure`,
`genexus_refactor`). Record the ranked list + what you chose to cover this pass in a
short comment or the PR description.

**Verify**: build `0 Erro(s)` after adding entries incrementally.

### Step 2: Add next_legal_actions for the chosen state-changing tools

For each, add a `BuildForX` following the existing helpers: after a successful
mutation, suggest the natural verify/next step (usually `genexus_lifecycle action=build`
on the affected target, then a preview or an inspect). Every suggested tool name MUST
be canonical (the 048 guard test enforces this). Keep ≤3 suggestions each.

**Verify**: build green; new `BuildFor*` cases return non-null for a representative success payload (add tests).

### Step 3: Add help entries for the chosen tools

Add `_helpTexts` entries (canonical keys) covering: what the tool does, its `action`
values (from `ToolIdentity.ActionsFor`), key args, defaults, and 2-3 examples. Match
the depth/format of the `genexus_query` entry.

**Verify**: `Get("genexus_refactor")` etc. return non-null (add assertions).

### Step 4: Full-suite gate

**Verify**: `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj -v:minimal` → all pass (including the 048 guard test over the new names).

## Done criteria

- [ ] `dotnet build` exits 0.
- [ ] `next_legal_actions` emitted for the chosen state-changing tools (new tests prove non-null).
- [ ] `ToolHelpCatalog.Get` non-null for the chosen tools (new assertions).
- [ ] 048 guard test still green (all new names are known to `ToolIdentity`).
- [ ] Full gateway suite passes.
- [ ] `plans/README.md` status row updated.

## STOP conditions

- 048 (registry) is not yet DONE — this plan builds on `ToolIdentity`; if it's absent,
  STOP and do 048 first (or fall back to hardcoded canonical names and note the debt).
- A chosen tool's "natural next step" isn't obvious from its schema — cover the ones
  that are clear, list the rest as follow-up rather than guessing a misleading suggestion.

## Maintenance notes

- Coverage is intentionally incremental — record what was left uncovered so the next
  pass continues. Consider a (non-failing) diagnostic test that lists tools with neither
  help nor a `_readOnlyTools`/opt-out marker, to track the shrinking gap.
- Reviewer: check that each new suggestion names a real canonical tool + a plausible
  action, and that no read-only tool gained spurious suggestions.
