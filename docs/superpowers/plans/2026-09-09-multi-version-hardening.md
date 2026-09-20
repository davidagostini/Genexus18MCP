# Multi-Version Hardening and Release Evidence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent silent KB/SDK mismatches, make version diagnostics truthful, automate per-major live evidence, and keep user-facing support documentation aligned with the catalog.

**Architecture:** Keep `config/gx-versions.json` as the only compatibility catalog. Add small CLI helpers for SDK identity and `.gxw` metadata, use the KB major to prioritize auto-discovery, and fail before writing a configuration when the selected SDK is incompatible. Keep the existing single-major live harness as the execution primitive and add a sequential catalog-driven wrapper that tests the same published artifact against every selected SDK.

**Tech Stack:** Node.js built-in modules and Node test runner; PowerShell 5+/7 scripts; GitHub Actions self-hosted Windows runner; Markdown generated/manual documentation.

---

### Task 1: Add shared CLI identity and KB compatibility guards

**Files:**
- Modify: `cli/lib/config.js:86-212, 1422-1437, 1551-1588`
- Modify: `cli/commands/axi.js:1-33, 553-590, 1191-1225, 1309-1420, 1643-1692`
- Test: `cli/run.test.js`

- [x] **Step 1: Add red tests for executable metadata, KB metadata, and mismatch rejection.**

  Add tests that create temporary directories containing a fake `GeneXus.exe` and a `.gxw` with `VersionNumber` `17.0.11.163677`. Inject a deterministic executable-version reader into the pure identity helper and assert that it returns `{ version: '18.0.10.184260', major: '18', source: 'executable-metadata' }`. Assert that the KB helper returns major `17`. Run `npm test -- --test-name-pattern='GeneXus|KB metadata|mismatch'` and verify the new assertions fail before implementation.

- [x] **Step 2: Implement catalog-aware version and KB metadata helpers.**

  Add these exported helpers to `cli/lib/config.js` without changing the existing string return contract of `readGeneXusVersionFromInstall`:

  ```js
  function getGeneXusMajor(version) {
      const match = String(version || '').match(/^\s*(\d+)/);
      return match ? match[1] : null;
  }

  function readGeneXusInstallationIdentity(gxPath, options = {}) {
      // Return { version, major, source } using version files, GeneXus.exe
      // ProductVersion/FileVersion, then the installation folder name.
  }

  function readGeneXusKbIdentity(kbPath) {
      // Read exactly one root .gxw, extract FriendlyVersion or VersionNumber,
      // and return { version, major, source, reason } without modifying the KB.
  }
  ```

  The executable fallback must use `execFileSync('powershell.exe', ...)` with a fixed script and the path supplied through an environment variable, never interpolated into shell source. Multiple `.gxw` files, malformed XML, or absent version fields must return a non-throwing unresolved identity.

- [x] **Step 3: Make auto-discovery prefer the KB major and validate the final pair.**

  Let `getGeneXusCatalogEntries(preferredMajor)` sort the requested major before the catalog primary while preserving primary-first behavior when no preference exists. Pass the detected KB major from `handleInit` and zero-config setup into `discoverGeneXusInstallation(preferredMajor)`. Before `createConfigFile` or zero-config writes, reject a known KB major whose selected SDK major differs, with a machine-readable `sdk_kb_mismatch` error, the detected values, and an explicit `--gx` remediation. When the KB major cannot be determined and SDK discovery is implicit, require explicit `--gx` rather than silently selecting the primary SDK.

- [x] **Step 4: Surface the same identity in doctor and support dumps.**

  Replace the CLI-only version-file read in `handleDoctor` and `buildSupportDump` with `readGeneXusInstallationIdentity`. Preserve existing `geneXus.version`, add `major` and `detectionSource`, and change the warning to say version metadata is unavailable rather than claiming that `version.txt` is required.

- [x] **Step 5: Run the focused CLI tests and inspect the diff.**

  Run `npm test -- --test-name-pattern='GeneXus|KB metadata|mismatch|init'`, then `npm run lint`. Confirm that explicit GX18/GX17 init paths remain accepted and that no config is written on mismatch.

### Task 2: Add catalog-driven live validation for the published artifact

**Files:**
- Create: `scripts/test-live-matrix.ps1`
- Create: `scripts/tests/test-live-matrix.test.ps1`
- Modify: `scripts/tests/run-release-script-tests.ps1`
- Modify: `.github/workflows/live-smoke.yml`
- Modify: `docs/live-kb-test-harness.md`
- Modify: `docs/release_protocol.md`

- [x] **Step 1: Add a contract test for the matrix runner.**

  Assert through PowerShell AST/source checks that the runner loads `scripts/gx-version-catalog.ps1`, accepts `-Majors`, accepts `-GxPathMap`, builds the primary artifact once unless `-SkipBuild` is supplied, invokes `scripts/test-live.ps1 -SkipBuild` once for every selected major, writes a summary, and returns nonzero when a selected major is unavailable or fails.

- [x] **Step 2: Implement sequential matrix execution with fail-closed summaries.**

  Implement `scripts/test-live-matrix.ps1` with mandatory `-KbPath` and `-FixtureManifest`, optional `-Majors`, `-GxPathMap` entries in `major=absolute-path` form, `-SkipBuild`, `-GatewayOnly`, `-RequireBuildAll`, `-RunBenchmark`, `-Iterations`, and `-SummaryPath`. Resolve each path from the explicit map or the catalog `defaultInstallPath`; reject duplicate/unknown majors and missing `genexus.exe`. Build once with the catalog primary SDK when needed, then run the existing single-major harness sequentially with `-SkipBuild`. Emit `gxmcp-live-matrix/1` containing catalog source, artifact mode, each major/path/status/exitCode/reason, and `allPassed`; never turn missing fixture, SDK, license, or SQL access into a pass.

- [x] **Step 3: Wire the self-hosted workflow to the matrix runner.**

  Added `majors` and `gx_path_map` inputs with safe defaults. The workflow keeps
  the summary in the runner's temporary directory, passes the map to the runner,
  and uploads the matrix summary and per-major benchmark artifacts. Keeping the
  summary path fixed avoids accepting an arbitrary artifact path from a manual
  dispatch. The job remains manual and self-hosted; ordinary hosted CI must not
  attempt proprietary SDK/KB access.

- [x] **Step 4: Document both single-major and matrix commands.**

  Document that the matrix validates the same published Gateway/Worker artifact against each selected SDK and that `live=unavailable` is an explicit infrastructure result. Include a GX17/GX18 example without credentials and retain the existing fixture-isolation rules.

- [x] **Step 5: Run script syntax and contract tests.**

  Run `pwsh -NoProfile -File scripts/tests/test-live-matrix.test.ps1`, `pwsh -NoProfile -File scripts/tests/run-release-script-tests.ps1`, and `npm run lint`. Run the actual matrix only when a verified disposable KB manifest is supplied; record unavailable status otherwise.

### Task 3: Align release and support documentation

**Files:**
- Modify: `docs/GETTING_STARTED.pt-br.md`
- Modify: `docs/GETTING_STARTED.es.md`
- Modify: `TROUBLESHOOTING.md`
- Modify: `docs/superpowers/plans/2026-09-08-gx-multi-version-support.md`
- Modify: `docs/superpowers/plans/2026-09-08-reliability-and-release-sync.md`
- Modify: `CHANGELOG.md`

- [x] **Step 1: Make localized setup examples version-neutral.**

  Keep GX18 as the catalog primary example, add a GX17 `--gx` example, and state that the SDK path must correspond to the KB major when more than one SDK is installed.

- [x] **Step 2: Reconcile plan status with evidence.**

  Mark implemented static/build/release-sync steps complete, leave live persistence/parity steps explicitly pending when no fixture was run, and record the exact validation commands and skip reason. Update the coverage/roadmap status line only where the current implementation changes its truth.

- [x] **Step 3: Add an Unreleased changelog entry.**

  Record the SDK/KB mismatch guard, executable metadata detection, and catalog-driven live matrix under `## Unreleased` using the existing release voice.

- [x] **Step 4: Run documentation and metadata checks.**

  Run `python scripts/sync-release-metadata.py --root . --version 3.1.0 --check`, `python scripts/verify-release-metadata.py --root . --version 3.1.0`, and `git diff --check`.

### Task 4: Release hygiene handoff

**Files:**
- No change to the pre-existing untracked `pnpm-lock.yaml`.

- [ ] **Step 1: Verify the release prerequisites without weakening the guard.**

  Confirm that the feature branch is ahead of `origin/main`, that the final implementation is committed before a formal release, and that the working tree contains no unrelated files. Do not pass `-AllowDirty` to hide the existing lockfile.

- [ ] **Step 2: Report the remaining owner decision.**

  Before release, the repository owner must decide whether the pre-existing `pnpm-lock.yaml` is removed outside this task, intentionally tracked, or kept as a local file with an agreed release workflow. The implementation must not make that decision silently.

---

## Verification checklist

- [x] `npm test`
- [x] `npm run lint`
- [x] `pwsh -NoProfile -File scripts/tests/run-release-script-tests.ps1`
- [x] `python -m unittest discover -s scripts/tests -v`
- [x] `dotnet test Genexus18MCP.sln --no-restore -v:q`
- [x] Worker build and focused compatibility tests with GX17
- [x] Worker build and focused compatibility tests with GX18
- [x] `python scripts/sync-release-metadata.py --root . --version 3.1.0 --check`
- [x] `python scripts/verify-release-metadata.py --root . --version 3.1.0`
- [x] `git diff --check`
- [x] Live matrix against a verified disposable KB, or explicit `live=unavailable` evidence

> **Validation refresh 2026-09-09:** `npm test` passed 90/90, the Python suite
> passed 41/41, the PowerShell release/live suite passed, the solution passed
> 3,817 tests with 16 skips and no failures, and GX17/GX18 Worker Release
> builds plus focused `PropertyService` tests passed. The controlled matrix
> invocation against `C:\KBs\KBTeste17` returned `live=unavailable` with two
> rows because no verified fixture manifest was supplied; no live mutation was
> attempted. Release hygiene remains pending because the final changes are not
> committed in this turn and the pre-existing untracked `pnpm-lock.yaml` still
> requires an owner decision.
