# Plan 041: Restore next_legal_actions for the consolidated create family

> **Executor instructions**: Follow this plan step by step. Run every verification
> command and confirm the expected result before moving on. If anything in "STOP
> conditions" occurs, stop and report — do not improvise. When done, update the
> status row for this plan in `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 4082fd3..HEAD -- src/GxMcp.Gateway/NextLegalActionsBuilder.cs src/GxMcp.Gateway/McpRouter.cs`
> If either file changed since this plan was written, compare the "Current state"
> excerpts against the live code before proceeding; on a mismatch, STOP.

## Status

- **Priority**: P1
- **Effort**: S-M
- **Risk**: LOW
- **Depends on**: none (see 046 for the durable version)
- **Category**: bug
- **Planned at**: commit `4082fd3`, 2026-07-23

## Why this matters

`next_legal_actions` is the array of suggested follow-up tool calls the gateway
attaches to state-changing responses so an agent doesn't have to guess the next
step. The builder switches on tool names `genexus_create_object`,
`genexus_create_popup`, and `genexus_save_as` — but those are **legacy aliases**.
The gateway rewrites them to `genexus_create` (with an `action` discriminator)
*before* the builder ever sees the name, and the builder has **no `genexus_create`
case**. So every object/popup/save-as done through the current tool surface emits
**zero** follow-up suggestions — the feature is silently dead for the most common
authoring flow. Separately, the suggestions that DO emit reference legacy alias
names (`genexus_undo`, `genexus_create_object`), which break when a user sets
`GXMCP_LEGACY_TOOL_ALIASES=0`.

## Current state

- `src/GxMcp.Gateway/NextLegalActionsBuilder.cs` — builds the suggestions. The
  dispatch switch (`:53-63`):
  ```csharp
  JArray? suggestions = toolName.ToLowerInvariant() switch
  {
      "genexus_apply_pattern" => BuildForApplyPattern(args, responsePayload, isError),
      "genexus_create_object" => isError ? null : BuildForCreateObject(args, responsePayload),
      "genexus_create_popup"  => isError ? null : BuildForCreatePopup(args, responsePayload),
      "genexus_edit"          => isError ? null : BuildForEdit(args, responsePayload),
      "genexus_lifecycle"     => BuildForLifecycle(args, responsePayload, isError),
      "genexus_save_as"       => isError ? null : BuildForSaveAs(args, responsePayload),
      "genexus_versioning"    => isError ? null : BuildForVersioning(args, responsePayload),
      _ => null,
  };
  ```
- `src/GxMcp.Gateway/McpRouter.cs` — `TryRewriteLegacyTool` maps the legacy names to
  the canonical `genexus_create` umbrella with an `action` (`:1334-1357`):
  ```csharp
  case "genexus_create_object": newArgs["action"] = "object";  newToolName = "genexus_create"; return true;
  case "genexus_create_popup":  newArgs["action"] = "popup";   newToolName = "genexus_create"; return true;
  case "genexus_save_as":       newArgs["action"] = "save_as"; newToolName = "genexus_create"; return true;
  ```
- The rewrite runs in `Program.RequestLoop.cs` (~`:220-231`) BEFORE dispatch and
  before `NextLegalActionsBuilder.BuildFor` is called (from
  `Program.ToolPayload.cs:491`), so `BuildFor` receives the **canonical** name
  (`genexus_create`), never the legacy `genexus_create_object`. The three legacy
  switch cases are therefore unreachable in normal operation.
- `genexus_versioning` IS canonical and handled — `genexus_undo` rewrites to
  `genexus_versioning action=undo`, which `BuildForVersioning` reads. That path works.
- Suggestions emitted today reference alias names, e.g. `BuildForEdit` suggests
  `genexus_undo` and `genexus_browser`; `BuildForCreateObject` suggests
  `genexus_create_object`. These only resolve because legacy aliases are ON by
  default.

- Convention: pure static builder, `Suggest(tool, args, why, priority)` helper,
  `S(token)` null-coalescing reader. Match it. Tests live in
  `src/GxMcp.Gateway.Tests/NextLegalActionsBuilderTests.cs` (if present) or add one.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | `0 Erro(s)` |
| Gateway tests | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~NextLegalActions" -v:minimal` | all pass |

## Scope

**In scope**:
- `src/GxMcp.Gateway/NextLegalActionsBuilder.cs`
- `src/GxMcp.Gateway.Tests/NextLegalActionsBuilderTests.cs` (create if absent)

**Out of scope**:
- `McpRouter.TryRewriteLegacyTool` — do NOT change the alias map.
- The suggestion CONTENT/wording of flows that already work (apply_pattern,
  lifecycle, versioning) beyond fixing alias tool-names in step 2.
- Building a shared tool-identity registry — that is the separate design plan 046.

## Steps

### Step 1: Add a canonical `genexus_create` case that dispatches by `action`

Add a `"genexus_create"` case to the switch that reads `args["action"]` and routes to
the existing per-flow builders:

```csharp
"genexus_create" => isError ? null : (args["action"]?.ToString()?.ToLowerInvariant() switch
{
    "object"  => BuildForCreateObject(args, responsePayload),
    "popup"   => BuildForCreatePopup(args, responsePayload),
    "save_as" => BuildForSaveAs(args, responsePayload),
    _ => null,
}),
```

Keep the three legacy cases (`genexus_create_object`/`_popup`/`genexus_save_as`) as
fall-throughs so a call made with legacy aliases disabled-but-name-passed still works
— OR remove them if you confirm they're unreachable. Prefer keeping them (harmless,
defensive). The `genexus_save_as` legacy case currently calls `BuildForSaveAs`;
ensure `BuildForSaveAs` reads `newName` from `args` the same way whether reached via
legacy `genexus_save_as` or canonical `genexus_create action=save_as` (the args are
identical post-rewrite — `newArgs` keeps the original keys).

**Verify**: `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` → `0 Erro(s)`.

### Step 2: Point emitted suggestions at canonical tool names

Audit every `Suggest("genexus_...", ...)` call in the file. Replace legacy alias
tool-names with the canonical umbrella name + `action`, so suggestions work even with
`GXMCP_LEGACY_TOOL_ALIASES=0`:

- `genexus_undo` → `genexus_versioning` with `args["action"]="undo"`.
- `genexus_create_object` → `genexus_create` with `args["action"]="object"`.
- `genexus_browser` — already canonical (it's the umbrella name); keep.
- `genexus_playbook` — verify this is a real tool name via
  `grep -n '"genexus_playbook"' src/GxMcp.Gateway/tool_definitions.json`. If it does
  not exist as a tool or legacy alias, replace the suggestion with `genexus_recipe` or
  drop it. If it IS a valid alias, leave it but prefer its canonical target.

Cross-check each suggested name against the alias map in `McpRouter.cs:1128-1420`
and the tool list. A suggestion that names a nonexistent tool is worse than none.

**Verify**: build green; `grep -n 'Suggest("genexus_undo"\|"genexus_create_object"' src/GxMcp.Gateway/NextLegalActionsBuilder.cs` returns nothing (all rewritten).

### Step 3: Tests

In `NextLegalActionsBuilderTests.cs`, add tests asserting:
- `BuildFor("genexus_create", { action:"object", name:"Foo", type:"Transaction" }, {}, false)`
  returns a non-null, non-empty array whose first suggestion's `tool` is a real tool
  name (e.g. `genexus_edit`).
- Same for `action:"popup"` and `action:"save_as"`.
- `BuildFor("genexus_create", {...}, {}, isError:true)` returns null.
- No emitted suggestion in any covered flow names a tool that isn't in the tool list
  / alias map (iterate the returned `tool` values; assert each is canonical). If a
  full tool-list assertion is heavy, at minimum assert none equals the known-legacy
  strings `"genexus_undo"`, `"genexus_create_object"`, `"genexus_save_as"`.

Model after existing gateway unit tests (plain xUnit `[Fact]`, `JObject` args).

**Verify**: `dotnet test ... --filter "FullyQualifiedName~NextLegalActions"` → all pass.

## Test plan

- New/expanded `NextLegalActionsBuilderTests`: canonical create dispatch (object/
  popup/save_as), error→null, and the "no legacy tool-name in output" guard.
- Pattern: existing gateway `[Fact]` tests.
- Verify: filtered gateway suite green with the new tests.

## Done criteria

- [ ] `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj` exits 0.
- [ ] `BuildFor("genexus_create", {action:"object",...})` returns a non-empty array (new test).
- [ ] No `Suggest(` call in `NextLegalActionsBuilder.cs` names `genexus_undo` /
      `genexus_create_object` / `genexus_save_as` (grep clean).
- [ ] Filtered gateway suite green; no files outside scope modified.
- [ ] `plans/README.md` status row updated.

## STOP conditions

- The switch or the alias map excerpts don't match live code (drift).
- `BuildForSaveAs`/`BuildForCreatePopup` read args under keys that the rewrite renamed
  (they should not — the rewrite only adds `action`, keeps original keys — but if you
  find a renamed key, report before guessing).
- `genexus_playbook` turns out to be a real, non-legacy tool you can't confirm — leave
  it and note it rather than deleting a working suggestion.

## Maintenance notes

- This is the tactical fix. Plan **046** proposes a single tool-identity registry that
  `NextLegalActionsBuilder`, `ToolHelpCatalog`, and `DidYouMean` all consult, so the
  next umbrella consolidation can't silently strand these switches again. If 046
  lands, this switch should be re-expressed in terms of the registry's canonical
  names + actions.
- Reviewer: verify the builder is invoked with the POST-rewrite (canonical) tool name
  — the fix depends on that ordering (`Program.RequestLoop.cs` rewrite precedes
  `Program.ToolPayload.cs` builder call).
