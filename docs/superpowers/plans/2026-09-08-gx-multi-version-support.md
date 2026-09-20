# GeneXus Multi-Version SDK Compatibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the MCP Worker build and run against GeneXus 17 and GeneXus 18 without compile-time dependence on SDK members that are version-specific, while establishing a small compatibility catalog that can receive future supported majors without changing the business services.

**Architecture:** Keep the existing MCP contracts and dispatcher routes. Add a Worker-side optional-SDK invocation layer for version-sensitive helper APIs, plus a pure Design System source parser used when the SDK helper surface is incomplete. Read DSO tokens/styles through the existing `PartAccessor`/`ISource` path. Add a Gateway-side version catalog that preserves the legacy `supportedMajor` field and adds additive multi-version metadata.

**Tech Stack:** C# (`net48` Worker, `net10.0-windows` Gateway), GeneXus Artech SDK, Newtonsoft.Json, xUnit, PowerShell build/runtime smoke tests.

---

- [x] Task 1: Add regression coverage for optional SDK members, source fallback, and version matching.
  - Files: `src/GxMcp.Worker.Tests/DesignSystemCompatibilityTests.cs`, `src/GxMcp.Gateway.Tests/WhoamiVersionTests.cs`.
  - Test missing optional methods without loading a real SDK helper; test token/class extraction from combined `tokens`/`styles` source and extraction diagnostics; test that GX17 and GX18 are supported and GX19 is not implicitly claimed.
  - Run the focused Worker and Gateway test filters and confirm the new tests fail for the current hard-coded GX18 Design System calls/version check.

- [x] Task 2: Implement reusable version and optional-SDK compatibility primitives.
  - Files: `src/GxMcp.Gateway/GeneXusVersionCatalog.cs`, `src/GxMcp.Worker/Compatibility/OptionalSdkInvoker.cs`, `src/GxMcp.Worker/Compatibility/DesignSystemSdkAdapter.cs`, `src/GxMcp.Worker/Helpers/DesignSystemSourceParser.cs`.
  - Keep the supported-major list in one catalog, parse a numeric major without prefix collisions, and expose a legacy primary major separately from the additive list.
  - Resolve `DesignSystemHelper` and invoke zero-argument members by name at runtime; missing members or invocation failures must be represented as unavailable so the adapter can fall back instead of crashing the dispatcher.
  - Move pure token/class parsing behind the parser while retaining the existing public `DesignSystemService` static methods as compatibility delegates.

- [x] Task 3: Refactor Design System reading to use the compatibility layer.
  - File: `src/GxMcp.Worker/Services/DesignSystemService.cs`.
  - Preserve `genexus_layout` action names, response fields, error codes, and GX18 SDK output when all helper members are available.
  - Add model scanning by name/type when index lookup cannot resolve a GX17 DSO, read both native source parts, and use source parsing per missing helper capability.
  - Add only additive diagnostics/source metadata identifying SDK versus source fallback; never return fabricated image/reference names.

- [x] Task 4: Make Gateway version reporting multi-version aware without breaking existing clients.
  - Files: `src/GxMcp.Gateway/Program.Whoami.cs`, `src/GxMcp.Gateway.Tests/WhoamiVersionTests.cs`.
  - Keep `geneXus.supportedMajor` as the existing primary-major value (`18`), add `supportedMajors` and `matchedMajor`, and compute `versionMatches` from the catalog.
  - Log the catalog rather than warning for a known GX17 installation.

- [x] Task 5: Document and record the compatibility change.
  - Files: `CHANGELOG.md` and the relevant GeneXus setup/validation section in `docs/agent_playbook.md`.
  - Document explicit `GX_PATH` builds for each installed SDK and the additive whoami fields; do not change release/deploy behavior or client configuration names.

- [x] Task 6: Validate the complete static/build matrix and clean up.
  - Built the Worker with `GX_PATH` set to GX17 and GX18, built the Gateway, ran focused compatibility tests, and ran the repository checks required by the changed projects.
  - Added the catalog-driven `scripts/test-live-matrix.ps1` so the same published artifact can be exercised once per selected SDK major; its contract and fail-closed behavior are covered by `scripts/tests/test-live-matrix.test.ps1`.
  - Reviewed `git diff`/`git status`, preserving unrelated changes such as the pre-existing untracked `pnpm-lock.yaml`.
- [ ] Live KB parity smoke remains pending: no verified disposable KB fixture and manifest were available in this run, so `whoami`, index/doctor, query/read, Design System persistence, and process-cleanup parity are not claimed as passing evidence. Run the matrix with `-KbPath` and `-FixtureManifest` when the fixture is provisioned.
