# Plan 051: Nexus IDE test + lint/typecheck baseline & gate (Phase 0)

> **Executor instructions**: Follow this plan step by step. Run every verification
> command and confirm the expected result before moving on. If a STOP condition occurs,
> stop and report. Update the status row in `plans/README.md` when done.
>
> **Drift check (run first)**: `git diff --stat 52e66f1..HEAD -- src/nexus-ide/package.json src/nexus-ide/src/test`

## Status

- **Priority**: P1 (foundation for all Nexus IDE work)
- **Effort**: M
- **Risk**: LOW (adds tests + a gate; touches no runtime provider code)
- **Depends on**: none
- **Category**: tests / dx
- **Planned at**: commit `52e66f1`, 2026-07-23
- **Roadmap**: `docs/nexus-ide-roadmap.md` Phase 0

## Why this matters

The `src/nexus-ide` extension has real users but only one test file (9 tests) and no CI
gate. Phases 1+ will change honest-but-broken providers (rename, references) that users
depend on — doing that without a safety net risks regressions. The infra already exists
(`@vscode/test-electron`, mocha, ESLint 9, `tsc`); this plan expands coverage to the
untested core and wires a repeatable local gate. This is the prerequisite for every later
Nexus IDE plan.

## Current state

- `src/nexus-ide/package.json` scripts: `compile` (`tsc -p ./`), `watch`, `pretest`
  (`npm run compile`), `lint` (`eslint src`), `test` (`node ./out/test/runTest.js`).
  Dev deps: `@vscode/test-electron ^2.5.2`, `mocha ^11`, `eslint ^9`, `typescript ^5.3`,
  `typescript-eslint ^8`.
- `src/nexus-ide/src/test/` — `runTest.ts` (electron harness), `suite/index.ts` (mocha
  glob loader), `suite/extension.test.ts` (378 lines, 9 real tests covering `GxUriParser`,
  `browseObjects`, `GxShadowService.materializeWorkspaceWithProgress`, `GxTreeProvider`
  grouping). Tests run with `NEXUS_IDE_TEST_MODE=1`.
- **Zero test coverage** of: `GxGatewayClient` (retry/session logic), `managers/BackendManager`
  (gateway spawn/lease), and all 13 language providers / 6 webviews.
- No CI workflow for the extension; `.github/workflows/` targets the .NET server.
  `@vscode/test-electron` needs a real VS Code download + display — runs locally /
  self-hosted, NOT on the repo's GitHub-hosted runners (same constraint as the Worker).

## Commands you will need

Run from `src/nexus-ide/` (the extension has its own `package.json` + `node_modules`):

| Purpose | Command | Expected |
|---------|---------|----------|
| Install deps | `npm install` (in `src/nexus-ide`) | exit 0 |
| Typecheck/compile | `npm run compile` | exit 0, `out/` populated |
| Lint | `npm run lint` | exit 0 (or a known baseline of warnings — record it) |
| Test | `npm test` | mocha runs; all pass |

## Scope

**In scope**:
- `src/nexus-ide/src/test/**` (new test specs + harness tweaks)
- `src/nexus-ide/package.json` (a `typecheck` script / `test:unit` split if useful; do NOT change runtime deps)
- A local gate script under `src/nexus-ide/` (e.g. `scripts/check.ps1`) OR a documented one-liner
- `docs/nexus-ide-roadmap.md` (tick Phase 0 status only)

**Out of scope**:
- Any provider / manager / webview RUNTIME code change (that's Phases 1+). Tests only.
- A GitHub-hosted CI job (impossible — needs VS Code + SDK). A self-hosted/local gate only.
- Refactoring the code-under-test to be more testable BEYOND minimal seams needed to unit-test
  (if a class can't be tested without a large refactor, note it as a Phase-1 follow-up, don't refactor here).

## Steps

### Step 1: Establish the current baseline

Run `npm install`, `npm run compile`, `npm run lint`, `npm test` in `src/nexus-ide` and
record the current state (compile clean? lint warning count? 9 tests pass?). This is the
"before" the gate protects.

**Verify**: paste the 4 command results; `npm test` shows the existing 9 tests passing.

### Step 2: Add unit coverage for the untested core

Prioritize by risk (logic that breaks silently). Add specs (mocha, mirror
`extension.test.ts` structure) for at least:
- `GxGatewayClient` — session-init/retry/content-unwrap logic. Where it does network I/O,
  inject a fake `http` transport or exercise the pure parsing/unwrap helpers (do NOT hit a
  real gateway in unit tests). If the class isn't seam-able without a small constructor
  injection, add the minimal seam and note it.
- `managers/BackendManager` — the path-resolution + lease logic (pure-ish parts), not the
  actual process spawn.
- 2–3 language providers with pure logic worth locking: `GxDefinitionProvider` local-`Sub`
  resolution, `GxSymbolProvider` regex extraction, `completionProvider` member-access parsing.
  Test the pure parts by feeding a fake document + a stubbed `provider.queryObjects`.

Do NOT chase 100% — target the highest-risk logic and the exact behaviors Phase 1 will touch.

**Verify**: `npm test` → all pass, with N new tests (state N). `npm run compile` clean.

### Step 3: Wire a repeatable local gate

Add a single command that runs compile + lint + test (e.g. `src/nexus-ide/scripts/check.ps1`
or an npm `check` script: `"check": "npm run compile && npm run lint && npm test"`).
Document it in the extension README's Development section as the pre-commit / pre-release gate.

**Verify**: the gate command runs all three and exits 0 (or documents the accepted lint-warning baseline).

### Step 4: Tick the roadmap

Mark Phase 0 status in `docs/nexus-ide-roadmap.md` current-plan-set table.

## Done criteria

- [ ] `npm run compile` in `src/nexus-ide` exits 0.
- [ ] `npm test` passes with new coverage on `GxGatewayClient` + `BackendManager` (path/lease logic) + ≥2 providers (state the new test count).
- [ ] A single `check` gate (script or npm script) runs compile+lint+test.
- [ ] No runtime (non-test) provider/manager code changed (`git status` shows only test/package/doc/script files).
- [ ] `plans/README.md` status row updated.

## STOP conditions

- A target class cannot be unit-tested without a substantial refactor of runtime code —
  add tests for what IS reachable, and record the untestable ones as a Phase-1 refactor
  follow-up rather than refactoring runtime code in this test-only plan.
- `@vscode/test-electron` can't run in the executor's environment (no display / no VS Code
  download) — report; the unit specs that don't need the electron host (pure-logic mocha)
  should still run via a lighter `mocha` invocation, so split those out and note the gap.

## Maintenance notes

- This gate is local/self-hosted by necessity. If a self-hosted runner is ever added for
  the Worker, the extension gate can join it.
- Reviewer: confirm no runtime behavior changed — this plan is a pure safety net. New tests
  should assert CURRENT behavior (characterization), so Phase 1 changes show up as
  intentional test diffs.
- After this lands, 052 and 053 can safely modify providers with the net in place.
