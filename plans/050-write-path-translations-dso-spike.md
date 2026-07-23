# Plan 050: Wire the deferred write paths — translations + DSO (spike)

> **Executor instructions**: This is a SPIKE. The deliverable is: confirm the SDK write
> API exists, wire ONE path end-to-end behind a confirmed verification, and document
> the other as a follow-up with findings. Do NOT bulk-wire everything blind. If the SDK
> doesn't expose a write the way expected, STOP and report (that's a valid finding — it
> may be an SDK wall). Update `plans/README.md` when done.
>
> **Drift check (run first)**: `git diff --stat cf736ec..HEAD -- src/GxMcp.Worker/Services/TranslationsService.cs src/GxMcp.Worker/Services/DesignSystemService.cs`

## Status

- **Priority**: P3
- **Effort**: M (per path)
- **Risk**: MED (SDK writes; verify before shipping)
- **Depends on**: 047 (live-KB harness — the only way to truly verify an SDK write)
- **Category**: direction / feature
- **Planned at**: commit `cf736ec`, 2026-07-23

## Why this matters

Two tools parse/read fine but silently drop their write half — a read→write asymmetry
an agent can't work around:
- **`genexus_db action=translations_import`** (`TranslationsService`): parses the CSV,
  then returns `status:"Unwired"` / `code:"ItemDeferred"` and lists every row under
  `skipped` with `reason:"write-path-deferred"` (`TranslationsService.cs:58-75`). The
  `CaptionExpression` SDK write is not wired.
- **`genexus_layout action=design_system`** (`DesignSystemService`): read-only; AGENTS.md
  notes "DSO write ops exist in the SDK but aren't wired." An agent can inspect a Design
  System Object's tokens/theme classes but not change them.

Wiring these closes the asymmetry so translations and design-system edits are authorable
through the MCP instead of requiring the GeneXus IDE.

## Current state

- `src/GxMcp.Worker/Services/TranslationsService.cs:58-81` — the deferred write:
  ```csharp
  // SDK write path not wired yet — record as skipped so the caller can see...
  skipped.Add(new JObject { ..., ["reason"] = "write-path-deferred" });
  ...
  result["status"] = "Unwired"; result["code"] = "ItemDeferred";
  result["hint"] = "CSV parsed; SDK CaptionExpression write path is not yet wired...";
  ```
  So the row model (`ObjectName`/`Attribute`/`Language`) is already parsed; only the
  per-object `CaptionExpression`/translation SDK mutation is missing.
- `src/GxMcp.Worker/Services/DesignSystemService.cs` — read path via `DesignSystemHelper`
  (token groups, theme classes, images, referenced DSOs). Read it to find the DSO object
  model and whether the helper (or the underlying SDK object) exposes setters.
- SDK service-resolution pattern for services not in the headless registry:
  `SdkServiceLocator.ConstructOrResolve` (see `docs/sdk_endpoints_roadmap.md` and the
  `reference_headless_service_registration_wall` note) — the same pattern the P0/P1
  endpoint expansion used. Use `genexus_sdk_probe` + grep the SDK raw dump to CONFIRM a
  write method exists before wiring (project rule: probe before claiming (un)supported).

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Set SDK path | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'` | (none) |
| Build worker | `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | `0 Erro(s)` |
| Probe SDK | `genexus_sdk_probe` (over a running gateway) + grep the raw dump for the write API | — |
| Verify (live) | the 047 harness with `GXMCP_TEST_KB` set | write round-trips |

## Scope

**In scope**:
- `src/GxMcp.Worker/Services/TranslationsService.cs` (wire the CaptionExpression write) — primary path.
- `src/GxMcp.Worker/Services/DesignSystemService.cs` (wire a DSO write) — secondary; may defer to follow-up.
- `docs/write-paths-translations-dso.md` — spike findings (which SDK API, what's verified, what's deferred).
- Worker tests + a 047-harness live verification for whichever path ships.

**Out of scope**:
- Folder/module placement (`Parent`/`Module` are confirmed no-op IL stubs — an SDK wall, not this).
- Any gateway routing change (the tools already exist; only the worker write is missing).

## Steps

### Step 1: Confirm the SDK write API (probe before wiring)

For translations: find how the GeneXus SDK sets a translated caption for
`(object, attribute, language)` — likely a `CaptionExpression`/translation entity on the
model or a translations service. Use `genexus_sdk_probe` + grep the SDK raw dump; do not
assume. For DSO: inspect `DesignSystemHelper` + the DSO object for setters.

**Verify**: doc §1 names the exact SDK type/method for at least the translations write,
with evidence it exists (probe output reference). If NO write API exists for a path,
mark that path an SDK wall and STOP wiring it (report as a finding).

### Step 2: Wire the translations write (primary)

Replace the `write-path-deferred` skip with the real SDK write: for each valid row,
resolve the object, apply the caption for the language, save. Preserve the existing
envelope shape (`updated` count, `skipped`, `errors`) — now `updated` reflects real
writes and `skipped` only holds genuinely-unwritable rows. Keep it transactional/best-effort
per the file's existing error handling. Gate a destructive bulk import behind the same
conventions other write tools use (dryRun/confirm if the file's siblings do).

**Verify**: `dotnet build` → `0 Erro(s)`; then a live round-trip via the 047 harness:
import a caption, read it back, confirm it persisted.

### Step 3: DSO write — wire if cheap, else document

If Step 1 found a clean DSO setter, wire one representative write (e.g. a token value)
with the same probe→wire→verify discipline. If it's more involved, document it as a
scoped follow-up in the doc with the findings, rather than half-wiring it.

**Verify**: whichever ships has a live round-trip; the deferred one is documented.

## Done criteria

- [ ] `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj` exits 0.
- [ ] Translations import performs a real SDK write (no more `status:"Unwired"` on valid rows),
      verified round-trip via the 047 harness (or STOP-reported as an SDK wall).
- [ ] `docs/write-paths-translations-dso.md` records the SDK API used + what's deferred.
- [ ] Worker suite green; live verification documented.
- [ ] `plans/README.md` status row updated.

## STOP conditions

- 047 (live-KB harness) is not available — you can build but cannot truly verify an SDK
  write; either land 047 first or ship build-only with an explicit caveat (like plans
  023/025/030/043) and say so.
- `genexus_sdk_probe` shows no write API for a path — that path is an SDK wall; report it
  (do NOT fabricate a write that silently no-ops, the exact anti-pattern this plan fixes).

## Maintenance notes

- Reviewer: insist on the live round-trip evidence for any path claimed "wired" — a
  build-only "write" that doesn't persist is worse than the honest `Unwired` it replaces.
- The `CaptionExpression` write, once found, may also benefit a future
  `genexus_properties`-style single-caption setter — note it if the API is general.
