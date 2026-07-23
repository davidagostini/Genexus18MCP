# Plan 044: Key tool-help by canonical names + resolve legacy aliases

> **Executor instructions**: Follow this plan step by step. Run every verification
> command and confirm the expected result before moving on. If anything in "STOP
> conditions" occurs, stop and report — do not improvise. When done, update the
> status row for this plan in `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 4082fd3..HEAD -- src/GxMcp.Gateway/ToolHelpCatalog.cs src/GxMcp.Gateway/McpRouter.cs`
> If either changed, compare "Current state" to live code; on a mismatch, STOP.

## Status

- **Priority**: P3
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none (shares the root cause with 041; see 046 for the durable fix)
- **Category**: tech-debt / agent-ergo
- **Planned at**: commit `4082fd3`, 2026-07-23

## Why this matters

`resources/read genexus://kb/tool-help/<toolName>` returns per-tool help via
`ToolHelpCatalog.Get(toolName)`. Three of the catalog's entries are keyed by
**pre-consolidation legacy names** (`genexus_create_object`, `genexus_db_optimize`,
and possibly `genexus_edit_and_build`). After the umbrella-tool consolidation an
agent asks for help by the canonical name (`genexus_create`, `genexus_db`), so the
lookup misses and returns `null` — help that exists is unreachable. This is the same
class of drift as plan 041 (curated catalogs left pointing at old tool names).

## Current state

- `src/GxMcp.Gateway/ToolHelpCatalog.cs` — a `Dictionary<string,string>` `_helpTexts`
  (`:7`, `StringComparer.OrdinalIgnoreCase`). Entry keys (`:9-254`):
  `genexus_query`, `genexus_lifecycle`, `genexus_edit`, `genexus_analyze`,
  `genexus_variable`, `genexus_read`, `genexus_apply_pattern`,
  **`genexus_create_object`** (`:198`), **`genexus_edit_and_build`** (`:230`),
  **`genexus_db_optimize`** (`:254`).
- Lookup (`:276`): `return _helpTexts.TryGetValue(toolName, out var text) ? text : null;`
- Consumer: `McpRouter.cs:903` — `string? text = ToolHelpCatalog.Get(toolName);`
  where `toolName` is the raw segment from the `genexus://kb/tool-help/<toolName>` URI.
- Legacy→canonical map is `McpRouter.TryRewriteLegacyTool` (`:1120-~1420`): e.g.
  `genexus_create_object → genexus_create (action=object)`,
  `genexus_db_optimize → genexus_db (action=optimize_*)`.
- **Verify which of the three keys are truly legacy**: run
  `grep -n '"name": "genexus_create_object"\|"name": "genexus_edit_and_build"\|"name": "genexus_db_optimize"' src/GxMcp.Gateway/tool_definitions.json`
  and check each against `TryRewriteLegacyTool`. `genexus_edit_and_build` may still be
  a current standalone tool (it appears in the live tool list) — if it is a real
  current tool name, LEAVE its key unchanged.

- Convention: verbatim multi-line string literals concatenated with `+`; keys are
  exact tool names. Tests: `src/GxMcp.Gateway.Tests/McpRouterTests.cs:687-696` already
  assert `ToolHelpCatalog.Get(name)` for the advertised names and `null` for unknown.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | `0 Erro(s)` |
| Gateway tests | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~ToolHelp|FullyQualifiedName~McpRouter" -v:minimal` | all pass |

## Scope

**In scope**:
- `src/GxMcp.Gateway/ToolHelpCatalog.cs`
- `src/GxMcp.Gateway.Tests/McpRouterTests.cs` or a `ToolHelpCatalogTests.cs` (add cases)

**Out of scope**:
- Writing NEW help entries for the ~30 tools with no help at all — that is a separate
  content task; note it as follow-up, don't do it here.
- `TryRewriteLegacyTool` — reuse for resolution, don't change it.
- The `GetGotchaHelp` path (`:884`) — unrelated.

## Steps

### Step 1: Re-key the confirmed-legacy entries to canonical names

For each of the three suspect keys confirmed legacy (per the tool_definitions +
alias-map check above), change the dictionary key to the canonical umbrella name and
update the help body's title/examples to name the canonical tool + `action`:
- `genexus_create_object` → `genexus_create` (body: note `action: object|popup|save_as|…`).
- `genexus_db_optimize` → `genexus_db` (body: note `action: optimize_analyze|optimize_suggest|optimize_report`).
- `genexus_edit_and_build` → leave as-is IF it is a real current tool; otherwise
  re-key to its canonical umbrella.

If two legacy names would collapse to the same canonical key (not expected here),
merge their bodies under one entry instead of overwriting.

**Verify**: `dotnet build ...` → `0 Erro(s)`.

### Step 2: Make `Get` alias-aware (so legacy requests still resolve)

So an agent that still asks for a legacy name gets help, add a fallback in
`ToolHelpCatalog.Get`:

```csharp
public static string? Get(string toolName)
{
    if (string.IsNullOrEmpty(toolName)) return null;
    if (_helpTexts.TryGetValue(toolName, out var text)) return text;
    // Legacy alias → canonical: resolve and retry so old tool names still find help.
    if (McpRouter.TryRewriteLegacyTool(toolName, null, out var canonical, out _)
        && _helpTexts.TryGetValue(canonical, out var canonText))
        return canonText;
    return null;
}
```

Confirm `TryRewriteLegacyTool` is accessible from `ToolHelpCatalog` (both are in the
`GxMcp.Gateway` namespace; it is `internal static`). If a null `args` argument is not
accepted by `TryRewriteLegacyTool`, pass `new JObject()`.

**Verify**: build `0 Erro(s)`.

### Step 3: Tests

Add/adjust tests:
- `Get("genexus_create")` returns non-null help (was null before).
- `Get("genexus_create_object")` (legacy) still returns non-null (via the fallback).
- `Get("genexus_db")` returns non-null.
- `Get("genexus_unknown_tool")` still returns null (existing assertion at
  `McpRouterTests.cs:696` must stay green).

**Verify**: `dotnet test ... --filter "FullyQualifiedName~ToolHelp|FullyQualifiedName~McpRouter"` → all pass.

## Test plan

- New cases: canonical hit, legacy-alias fallback hit, unknown miss.
- Pattern: `McpRouterTests.cs:687-696`.
- Verify: filtered gateway suite green.

## Done criteria

- [ ] `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj` exits 0.
- [ ] `Get("genexus_create")` and `Get("genexus_db")` return non-null (new tests).
- [ ] `Get("genexus_create_object")` still returns non-null via fallback.
- [ ] `Get("genexus_unknown_tool")` returns null.
- [ ] No files outside scope modified.
- [ ] `plans/README.md` status row updated.

## STOP conditions

- The catalog keys or the `Get` body don't match live code (drift).
- `genexus_edit_and_build` turns out ambiguous (both a current tool AND an alias) —
  report; do not guess which wins.
- `TryRewriteLegacyTool` isn't reachable/`internal`-visible from `ToolHelpCatalog` —
  report (the alternative is a small local legacy→canonical map, but confirm first).

## Maintenance notes

- Plan **046** (tool-identity registry) is the durable fix: help, next_legal_actions,
  and DidYouMean should all resolve names through one canonical registry. This plan is
  the tactical patch until then.
- Follow-up (not in scope): the catalog covers ~10 of ~40 tools. Consider a test that
  fails when a tool in `tool_definitions.json` has neither help nor an explicit
  "no-help-needed" opt-out, to stop the gap from growing.
