# Nexus IDE — elevation roadmap (to MCP-server quality bar)

Goal: bring the `src/nexus-ide` VS Code extension up to the same maturity bar as the MCP
server (tested, honest, robust, no dead code). The extension has **real users**, so the
ordering prioritizes (1) not regressing them and (2) fixing what currently *looks like it
works but doesn't* — before adding features.

Grounded in `docs/nexus-ide-recon.md` (the verified gap map). Release is **tied to the
MCP** — the extension ships in lockstep with the server, not on its own cadence (see
Phase 4).

## Why phased (honest expectation)

The MCP server reached its quality bar over 6+ audit passes + ~2200 tests + CI gates.
The extension today has 9 tests (one file), no CI, dead code, and several honest-but-
broken stubs. "Same level" is a multi-step program, not a single pass. Each phase is
read-only-planned first, then executed with review — the same flow used for MCP plans
040–046.

## Phases

### Phase 0 — Verification baseline (foundation) — **plan 051**
The audit rule: a verification baseline is prerequisite #1 before risky changes. The
test/lint/compile *infra already exists* (`package.json` scripts `compile`/`lint`/`test`,
`@vscode/test-electron`, ESLint 9) — Phase 0 EXPANDS coverage to the untested core
(`GxGatewayClient`, `BackendManager`, the language providers) and wires a local/self-hosted
lint+typecheck+test gate. (VS Code tests can't run on GitHub-hosted CI here — same
local-only constraint as the Worker.)

### Phase 1 — Correctness & honesty (user-facing now) — **plans 052, 053**
The "looks like it works but doesn't" set real users hit today:
- **052**: rename shows nothing in the editor (`renameProvider.ts:74` returns an empty
  `WorkspaceEdit`); references/definitions collapse to object-at-(0,0)
  (`referenceProvider.ts:29`, `definitionProvider.ts:55`); variable references unsupported.
- **053**: `SyncManager` (complete SSE live-sync, `managers/SyncManager.ts`) is imported
  but never `register()`ed — wire it (if the gateway emits `notifications/resources/updated`)
  or delete it; fix the mis-wired "Explain Code with AI" → `copyMcpConfig`
  (`gxActionsProvider.ts:56`); plus logging/bare-catch hygiene quick-wins.

### Phase 2 — Robustness / hygiene (production-grade) — *future plans*
- Structured logging (VS Code `OutputChannel` + level gating) replacing 49 `console.log`.
- Audit the 11 bare `catch {}` — surface real read/parse failures.
- Webview CSP + bundle `mermaid` locally (`DiagramView.ts:61` loads it from a CDN, no CSP).
- Remove dev-tree path assumptions from packaged resolution (`gxShadowService.ts:76-79`,
  `BackendManager.ts:270-296`).

### Phase 3 — Feature completeness (the "IDE" promise) — *future plans*
- Real inline completion backed by MCP/`genexus_ai_complete` (today: 4 hardcoded strings).
- Code actions driven by the diagnostics the linter already produces (today: 1 hardcoded fix).
- `LayoutView` editor (or honest read-only labeling).

### Phase 4 — Release discipline (tied to the MCP) — *future plan*
Decision (2026-07-23): the extension versions and ships **with the MCP**, one cycle.
Practical work: fold VSIX build/package into the MCP release flow (`release.ps1` /
`.github/workflows/release.yml`) so a server release also produces/publishes the matching
extension VSIX; reconcile the extension's own `version` field (currently `1.1.0`) with the
MCP version policy; add a CHANGELOG section (or shared CHANGELOG entries) for extension-
facing changes. Deferred until Phases 0–1 land.

## Current plan set

| Plan | Phase | Title | Status |
|------|-------|-------|--------|
| 051 | 0 | Nexus IDE test + lint/typecheck baseline & gate | DONE |
| 052 | 1 | Honest rename + real reference/definition locations | TODO |
| 053 | 1 | Resolve SyncManager + fix mis-wired command + hygiene | TODO |

Phases 2–4 are described above but NOT yet written as plans — they become plans once
Phase 0–1 land and the baseline is protecting the changes.

## Ordering rationale

051 (baseline) first so 052/053 land on a safety net. 052 before 053 (the rename/refs
lies are the most user-visible). Phases 2–4 after, lowest-user-risk-last. Everything is
local/self-hosted for build+test (GeneXus SDK + `@vscode/test-electron` can't run on
GitHub-hosted CI).
