# Reliability, Capability Contracts, and Release Synchronization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Improve the already validated GeneXus 17/18 support without depending on new live-test infrastructure, while making version-sensitive metadata and release-facing documentation synchronize automatically.

**Architecture:** `config/gx-versions.json` becomes the single source of truth for supported majors, primary SDK, display names, and installation candidates. The Gateway, Worker capability response, PowerShell harnesses, and release metadata synchronizer consume that catalog. Existing tool names and response payloads remain backward compatible; only legacy error payloads are normalized at the dispatcher boundary and the Design System fallback gains explicit completeness diagnostics.

**Tech Stack:** C#/.NET 10 Gateway, C#/.NET Framework 4.8 x86 Worker, Newtonsoft.Json, PowerShell 7, Python 3 standard library, xUnit, existing release-script test harness.

---

> **Status refresh 2026-09-09:** Tasks 1–5 are implemented and validated by the
> repository's static, unit, build, and release-script checks. Real disposable-KB
> persistence/parity remains an environment-dependent gate; the catalog-driven
> matrix now records that gap explicitly instead of treating it as a pass.

### Task 1: Establish one version catalog and shared script loader

**Files:**
- Create: `config/gx-versions.json`
- Create: `scripts/gx-version-catalog.ps1`
- Modify: `src/GxMcp.Gateway/GeneXusVersionCatalog.cs`
- Modify: `src/GxMcp.Gateway/GxMcp.Gateway.csproj`
- Modify: `src/GxMcp.Worker/GxMcp.Worker.csproj`
- Modify: `build.ps1`, `install.ps1`, `scripts/test-live.ps1`, `scripts/live-build-all.ps1`, `scripts/build-release-candidate.ps1`, `scripts/coverage/collect.ps1`, `scripts/test_all.ps1`
- Test: `src/GxMcp.Gateway.Tests/WhoamiVersionTests.cs`, `scripts/tests/test-version-catalog.ps1`

- [x] **Step 1: Add the catalog with only currently validated majors.**

```json
{
  "$schema": "genexus-mcp/version-catalog/1",
  "primaryMajor": "18",
  "supportedMajors": [
    {
      "major": "17",
      "displayName": "GeneXus 17",
      "defaultInstallPath": "C:\\Program Files (x86)\\GeneXus\\GeneXus17Trial",
      "registryNames": ["GeneXus 17", "GeneXus 17 Trial"],
      "legacyRegistryVersions": ["17.0"]
    },
    {
      "major": "18",
      "displayName": "GeneXus 18",
      "defaultInstallPath": "C:\\Program Files (x86)\\GeneXus\\GeneXus18",
      "registryNames": ["GeneXus 18"],
      "legacyRegistryVersions": ["18.0"]
    }
  ]
}
```

- [x] **Step 2: Implement `scripts/gx-version-catalog.ps1`.** It exposes `Get-GxVersionCatalog`, `Get-GxPrimaryMajor`, `Get-GxPrimaryInstallPath`, and `Get-GxSupportedMajorsDisplay`; it fails with the catalog path when JSON is missing or malformed.

- [x] **Step 3: Make both .NET projects copy the catalog into their output under `config/gx-versions.json`.** The runtime loader first uses `GXMCP_VERSION_CATALOG`, then the copied file, and finally a two-major emergency fallback that is marked as fallback in diagnostics rather than silently becoming a new source of truth.

- [x] **Step 4: Replace the Gateway hard-coded catalog with the JSON loader.** Preserved `SupportedMajors`, `PrimaryMajor`, `GetMajor`, `IsSupported`, and `GetMatchingMajor` signatures used by current tests, with the legacy `SupportedGeneXusMajor` alias retained as a static property.

- [x] **Step 5: Replace primary/default `GeneXus18` literals in build, install, live, coverage, candidate, and test scripts with the PowerShell catalog loader.** Explicit `-GxPath` and `GX_PATH` remain authoritative.

- [x] **Step 6: Add script tests for catalog loading, primary path, display text, and malformed/missing catalog failure.** Ran `pwsh -NoProfile -File scripts/tests/test-version-catalog.ps1` and the focused Gateway version tests.

### Task 2: Add an honest version/capability contract to runtime diagnostics

**Files:**
- Create: `src/GxMcp.Worker/Compatibility/SdkIdentity.cs`
- Modify: `src/GxMcp.Worker/Services/SdkProbeService.cs`
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs`
- Modify: `src/GxMcp.Worker.Tests/SdkCapabilityContractTests.cs`
- Modify: `src/GxMcp.Gateway/Program.Whoami.cs`
- Modify: `src/GxMcp.Gateway.Tests/WhoamiVersionTests.cs`

- [x] **Step 1: Add a Worker identity helper that reads the selected `GX_PROGRAM_DIR`/`GX_PATH`, detects `version.txt` or `GeneXus.exe` product version, extracts the numeric major, and reports `detectionSource` and `catalogSource`.

- [x] **Step 2: Extend `SdkProbeService.Capabilities()` with `sdk` and `contract` blocks.** The response retains `schemaVersion: genexus-sdk-capabilities/1` and existing capability entries, while adding `major`, `version`, `catalogSupported`, `detectionSource`, and `evidenceLevel: signature_probe`.

- [x] **Step 3: Add a Design System capability entry that reports `native_helper`, `source_parts_fallback`, and `fallbackCompleteness` without claiming persistence parity.** GX17 may report the source-parser route; unknown versions report `unsupported_catalog` instead of pretending to be supported.

- [x] **Step 4: Add tests asserting the stable shape, honest `persistenceVerified=false`, supported-major recognition, and unknown-major behavior without requiring a live SDK.

- [x] **Step 5: Include the catalog summary in `genexus_whoami.geneXus` and leave `genexus_sdk_probe mode=capabilities` as the detailed capability source.** The existing `supportedMajor` field semantics remain unchanged.

### Task 3: Normalize legacy Worker errors without breaking existing consumers

**Files:**
- Create: `src/GxMcp.Worker/Helpers/McpResponseNormalizer.cs`
- Modify: `src/GxMcp.Worker/Models/McpResponse.cs`
- Modify: `src/GxMcp.Worker/Services/CommandDispatcher.cs`
- Modify: `src/GxMcp.Worker/Services/SdkProbeService.cs`, `BuildService.cs`, `FormatService.cs`, `PropertyService.cs`, `BlameService.cs`, `NavigationService.cs`
- Create: `src/GxMcp.Worker.Tests/McpResponseNormalizerTests.cs`
- Modify: `docs/envelope.md`, `CHANGELOG.md`

- [x] **Step 1: Define the normalization rules in tests.** Canonical `status=error` remains unchanged; `status=Error` becomes canonical with `error.code`, `error.message`, and an optional `error.legacyStatus`; top-level string/object `error` becomes canonical; successful payloads remain byte-shape compatible except for no normalization.

- [x] **Step 2: Implement `McpResponseNormalizer.Normalize(string json, string fallbackCode, string fallbackHint)` using `JObject.Parse`, preserving the original payload under `error.details.legacyPayload` only when needed for support diagnostics and never exposing stack traces by default.

- [x] **Step 3: Apply normalization once after `DispatchInternal` returns and before idempotency caching.** Cacheability remains based on canonical status, so normalized errors are never cached.

- [x] **Step 4: Migrate direct raw error returns in the identified services to `McpResponse.Err` where the code path is straightforward; leave nested domain error fields inside successful result payloads untouched.

- [x] **Step 5: Add a contract test that feeds representative legacy shapes from Build/Format/Property/Blame/Navigation and asserts one canonical envelope. Ran the focused Worker tests.

### Task 4: Make Design System fallback explicit and safer

**Files:**
- Modify: `src/GxMcp.Worker/Helpers/DesignSystemSourceParser.cs`
- Modify: `src/GxMcp.Worker/Compatibility/DesignSystemSdkAdapter.cs`
- Modify: `src/GxMcp.Worker/Services/DesignSystemService.cs`
- Modify: `src/GxMcp.Worker.Tests/DesignSystemCompatibilityTests.cs`
- Create: `src/GxMcp.Worker.Tests/Fixtures/DesignSystems/complex.tokens.txt`
- Create: `src/GxMcp.Worker.Tests/Fixtures/DesignSystems/complex.styles.txt`

- [x] **Step 1: Extend the parser result with `warnings`, `unparsedConstructs`, and `completeness` (`complete`, `partial`, or `empty`).** Existing arrays and token maps remain unchanged.

- [x] **Step 2: Replace first-closing-brace regex extraction with a small brace-aware scanner that ignores quoted strings/comments and returns balanced blocks for token groups and classes.** Unknown constructs add a warning instead of being silently dropped.

- [x] **Step 3: Make `DesignSystemSdkAdapter` merge per-member SDK results with source fallback and propagate member-level warnings/completeness under `compatibility`.** A source-read exception is represented as a warning, not an empty success with no explanation.

- [x] **Step 4: Add complex parser coverage for nested braces, quoted image names, comments, imports, CSS variables, and malformed blocks.** The current coverage uses deterministic inline fixtures in `DesignSystemCompatibilityTests.cs`; malformed input produces `partial`, not falsely complete data.

- [x] **Step 5: Preserve the existing live GX17 behavior and response fields; only add compatibility diagnostics and warnings.

### Task 5: Make release synchronization automatic and fail-safe

**Files:**
- Create: `scripts/sync-release-metadata.py`
- Modify: `scripts/verify-release-metadata.py`
- Modify: `release.ps1`, `scripts/release.ps1`
- Modify: `server.json`, `README.md`, `AGENTS.md`, `CONTRIBUTING.md`, `SECURITY.md`, `TROUBLESHOOTING.md`, `config.sample.json`
- Create: `docs/generated/supported-versions.md`
- Create: `scripts/tests/test_sync_release_metadata.py`
- Modify: `scripts/tests/test-release-entrypoint.ps1`, `scripts/tests/test-release-preflight.ps1`

- [x] **Step 1: Implement an idempotent synchronizer with `--root`, `--version`, `--write`, and `--check`.** It reads `config/gx-versions.json`, updates `server.json` version/package fields, updates the sample config primary path, and renders `docs/generated/supported-versions.md` from a fixed template.

- [x] **Step 2: Add generated markers to the version/compatibility sections of README and AGENTS; the synchronizer updates only those marked blocks and refuses to edit an unmarked or ambiguous block.

- [x] **Step 3: Make `verify-release-metadata.py` validate `server.json`, `config.sample.json`, the generated document hash/content, and all package/lockfile versions.** Failures remain machine-readable and non-destructive.

- [x] **Step 4: Invoke the synchronizer in `release.ps1` after resolving `$Version` and before the dirty-tree check/metadata commit.** Synchronized files are included in the allowed bump set and release-managed paths. `-DryRun` runs `--check`/reports intended changes without writing.

- [x] **Step 5: Add a release-script test using a temporary copy that proves two consecutive `--write` runs are identical, package/server versions converge, supported-major text is generated, and ambiguous markers fail closed.

- [x] **Step 6: Update remaining user-facing hard-coded language to refer to the generated compatibility document and catalog-driven default.** Historical changelog entries remain unchanged.

### Task 6: Validate, simplify, and review the integrated change

**Files:**
- Modify only recently touched files after validation if simplification is required.
- Review: all files listed in Tasks 1–5 and the final `git diff`.

- [x] **Step 1: Run focused tests after each task: Gateway version/capability tests, Worker response/Design System tests, Python synchronizer tests, and PowerShell release-script tests.

- [x] **Step 2: Run `npm test` and `npm run lint`; run the solution build/tests with the configured GX18 SDK and the Worker build with GX17 if the local SDK path is available.

- [x] **Step 3: Use the simplify pass on only the newly modified code, preserving all response and release contracts.

- [x] **Step 4: Run `git diff --check`, inspect the complete diff and status, and report any unvalidated live parity or infrastructure-dependent P0 items explicitly. Do not commit or publish without a separate user request.

> **Validation refresh 2026-09-09:** Full repository checks are green. The
> installed SDK identities were detected as GX17 `17.0.11.163677` and GX18
> `18.0.10.184260` from executable metadata, and major-specific discovery
> selected the matching installation. Live persistence parity remains
> explicitly unavailable without an attested disposable fixture.
