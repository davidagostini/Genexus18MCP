# Plan 042: Make aggregates + empty-state universal across list-returning tools

> **Executor instructions**: Follow this plan step by step. Run every verification
> command and confirm the expected result before moving on. If anything in "STOP
> conditions" occurs, stop and report — do not improvise. When done, update the
> status row for this plan in `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 4082fd3..HEAD -- src/GxMcp.Gateway/Program.ToolPayload.cs`
> If it changed, compare the "Current state" excerpt against live code; on a
> mismatch, STOP.

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: LOW-MED
- **Depends on**: none
- **Category**: agent-ergo
- **Planned at**: commit `4082fd3`, 2026-07-23

## Why this matters

The gateway enriches list responses with `returned`, `empty`, `totalByType`,
`hasMore`, and `nextOffset` so an agent can tell "0 results" from a silent failure
and can paginate without guessing. But the enrichment only fires when the response
carries its collection under one of eight hard-coded **top-level** keys
(`results/objects/items/tools/checks/entries/nodes/controls`). Tools whose collection
lives under a different key — `endpoints` (`genexus_api`), `history`
(`genexus_versioning history`), `snapshots` (`genexus_kb validate`) — or nested one
level down under the canonical `result` object (the shape every `McpResponse.Ok`
producer emits: gxserver `pending`/`ignored`/`conflicts`, module/version lists) get
**no** aggregates and **no** explicit empty flag. Against those tools an agent cannot
distinguish an empty list from an error. This plan extends the same enrichment to
those collections.

## Current state

- `src/GxMcp.Gateway/Program.ToolPayload.cs`, `NormalizeToolPayloadForAxi(...)` — the
  enrichment loop (`:388-445`):
  ```csharp
  string[] collectionKeys = { "results", "objects", "items", "tools", "checks", "entries", "nodes", "controls" };
  foreach (var key in collectionKeys)
  {
      if (obj[key] is not JArray arr) continue;
      // ... projection (query/list_objects only) ...
      if (meta["totalByType"] == null) { var t = BuildTotalsByType(arr); if (t.Properties().Any()) meta["totalByType"] = t; }
      int returned = arr.Count;
      if (obj["returned"] == null) obj["returned"] = returned;
      if (obj["empty"] == null) obj["empty"] = returned == 0;
      if ((obj["empty"]?.Value<bool>() ?? false)) EnsureEmptyStateHelp(obj, toolName);
      // ... total / hasMore / nextOffset ...
      break;
  }
  ```
- The loop reads `obj[key]` — **top level only**. `SearchService` (genexus_query /
  list_objects) surfaces its array at the top level as `obj["results"]`
  (`SearchService.cs:569,618`), which is why those tools work today.
- Tools that return via `McpResponse.Ok(result: new JObject { ["endpoints"] = arr })`
  nest the array at `obj["result"]["endpoints"]` — invisible to the loop. Confirmed
  producers: `ApiIntrospectService.cs:89` (`endpoints`), `HistoryService.cs:491`
  (`history`), `KbValidationService.cs:186` (`snapshots`); gxserver
  `pending`/`ignored`/`conflicts` envelopes; module/version lists.
- `BuildTotalsByType(JArray)` (`:530`) counts by each row's `type` field —
  works on any array of objects; returns empty when rows lack `type` (then it isn't attached).
- `EnsureEmptyStateHelp(obj, toolName)` (`:507`) appends a help string; safe to call
  for any tool.

- Convention: the method mutates `obj`/`meta` in place, guards every write with
  `if (obj[x] == null)`, and only attaches `meta` when non-empty (`:450-457`). Match
  it — additive, never overwrite an existing field.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build gateway | `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` | `0 Erro(s)` |
| Gateway tests | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --filter "FullyQualifiedName~ToolPayload|FullyQualifiedName~Normalize|FullyQualifiedName~Axi" -v:minimal` | all pass |
| Full gateway suite | `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj -v:minimal` | all pass |

## Scope

**In scope**:
- `src/GxMcp.Gateway/Program.ToolPayload.cs` (the enrichment loop + a small helper)
- `src/GxMcp.Gateway.Tests/` — the test file that already covers
  `NormalizeToolPayloadForAxi` (find it: `grep -rln "NormalizeToolPayloadForAxi\|totalByType\|\"empty\"" src/GxMcp.Gateway.Tests`). Add cases there.

**Out of scope**:
- **Field projection** — leave `ShouldProjectFieldsForTool` / `ProjectArrayItems`
  scoped to query + list_objects. This plan adds aggregates/empty ONLY, not the
  compact field allowlist (that is a separate, rejected finding — small lists don't
  benefit).
- The worker services that produce these envelopes — do NOT change their shape.
- The `results` top-level fast-path — keep it working exactly as today.

## Steps

### Step 1: Add the nested + broadened collection keys

Extend `collectionKeys` with the confirmed additional collection names, and add a
second scan that descends into a top-level `result` object when no top-level
collection matched. Keep it deterministic — do NOT auto-detect "the sole array
property" (that would wrongly pick up per-row sub-arrays like `endpoints[i].parms`).

Target shape:

```csharp
string[] collectionKeys = {
    "results", "objects", "items", "tools", "checks", "entries", "nodes", "controls",
    // Additional primary-collection keys used by non-search tools:
    "endpoints", "history", "snapshots", "versions", "modules",
    "pending", "ignored", "conflicts", "targets", "pipelines"
};

// Where the collection lives: top-level first (search tools), then inside the
// canonical `result` object (McpResponse.Ok producers).
JObject collectionHost = obj;
string? matchedKey = collectionKeys.FirstOrDefault(k => obj[k] is JArray);
if (matchedKey == null && obj["result"] is JObject resultObj)
{
    matchedKey = collectionKeys.FirstOrDefault(k => resultObj[k] is JArray);
    if (matchedKey != null) collectionHost = resultObj;
}
```

Then run the existing enrichment body against `arr = (JArray)collectionHost[matchedKey]`.
**Write the aggregate fields (`returned`, `empty`, `totalByType`, `hasMore`,
`nextOffset`) onto the TOP-LEVEL `obj`** (not inside `result`), so an agent finds them
in the same place regardless of where the collection nested. `EnsureEmptyStateHelp`
and the `total`/`limit`/`offset` reads stay as-is (they already read `obj[...]` /
`toolArgs`). Replace the `foreach (var key ...)` structure with the single
`matchedKey`/`collectionHost` resolution above; keep the `if (matchedKey == null) `
guard so nothing runs when there's no collection.

Keep projection gated: only call `ProjectArrayItems`/set `meta["fields"]` when
`collectionHost == obj` AND `ShouldProjectFieldsForTool(toolName)` (i.e. don't
project nested collections — out of scope).

**Verify**: `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj -v:minimal` → `0 Erro(s)`.

### Step 2: Tests for the new shapes

In the gateway test file covering `NormalizeToolPayloadForAxi`, add cases:
- A payload `{ status:"ok", result:{ endpoints:[{type:"Api"},{type:"Api"}] } }` for
  toolName `genexus_api` → asserts top-level `returned==2`, `empty==false`,
  `meta.totalByType.Api==2`.
- A payload `{ status:"ok", result:{ endpoints:[] } }` → asserts `returned==0`,
  `empty==true`, and that empty-state help is attached.
- A top-level `{ results:[...] }` (query) still enriches exactly as before (guard
  against regression).
- A payload with NO collection (`{ status:"ok", result:{ note:"x" } }`) → no
  `returned`/`empty`/`meta` added.

Model after the existing `NormalizeToolPayloadForAxi` tests in that file.

**Verify**: filtered gateway tests pass; then run the FULL gateway suite (Step 3).

### Step 3: Full-suite regression gate

Because this touches a code path every tool response flows through, run the whole
gateway suite and confirm no existing envelope test regressed.

**Verify**: `dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj -v:minimal`
→ all pass (same pass count as before + your new tests; 0 failures).

## Test plan

- New tests: nested `result.endpoints` non-empty, nested empty, top-level `results`
  regression, no-collection no-op.
- Pattern: existing `NormalizeToolPayloadForAxi` tests.
- Verify: full gateway suite green.

## Done criteria

- [ ] `dotnet build src\GxMcp.Gateway\GxMcp.Gateway.csproj` exits 0.
- [ ] A `genexus_api`-shaped `{result:{endpoints:[...]}}` payload gets top-level
      `returned`/`empty`/`meta.totalByType` (new test proves it).
- [ ] Top-level `results` enrichment unchanged (regression test green).
- [ ] Full gateway suite passes with 0 failures.
- [ ] No files outside scope modified.
- [ ] `plans/README.md` status row updated.

## STOP conditions

- The enrichment loop excerpt doesn't match live code (drift).
- A full-suite test fails because some existing tool already emits one of the newly
  added keys (`endpoints`/`history`/…) as a per-row sub-array at the *top level* or
  inside `result` in a way that mis-triggers enrichment — investigate that tool's
  shape and narrow the key rather than forcing it.
- You find a tool whose primary collection is nested TWO levels deep (not directly
  under `result`) — report it; this plan only descends one level.

## Maintenance notes

- New umbrella tools that return a collection under a novel key must add that key to
  `collectionKeys` to get aggregates. Consider documenting the blessed-key list in
  `AGENTS.md` near the AxiCompact section.
- Reviewer: confirm aggregate fields land at top level (not buried under `result`)
  and that projection was NOT extended to nested collections.
- Deferred: field projection for non-search list tools (rejected finding ERGO-04 —
  small lists, marginal savings). Revisit only if a nested tool starts returning
  large per-item shapes.
