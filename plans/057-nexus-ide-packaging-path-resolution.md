# Plan 057: Fix dev-tree path assumptions in packaged resolution (Phase 2)

> **Executor instructions**: Follow step by step, verify each, STOP-and-report on STOP
> conditions, update `plans/README.md` when done. Minimal narration; final report in one message.
>
> **Drift check (run first)**: `git diff --stat f98ecd0..HEAD -- src/nexus-ide/src/gxShadowService.ts src/nexus-ide/src/managers/BackendManager.ts`

## Status

- **Priority**: P3
- **Effort**: M
- **Risk**: MED (path resolution drives whether the packaged extension can find its backend)
- **Depends on**: 051 (test baseline)
- **Category**: bug / dx (packaging correctness)
- **Planned at**: commit `f98ecd0`, 2026-07-23
- **Roadmap**: `docs/nexus-ide-roadmap.md` Phase 2

## Why this matters

Two resolution paths bake the **monorepo dev-tree layout into shipped code**, so behavior in a
packaged VSIX (what real users install) differs from dev and can misresolve:
- `gxShadowService.ts:76-79` — when no workspace is open, it walks up from `__dirname` looking
  for `.git` or `Genexus18MCP.sln` to guess a workspace root. In an installed extension there is
  no repo → this guess is meaningless / can point somewhere wrong.
- `managers/BackendManager.ts:270-296` — hardcodes three gateway-exe fallback locations:
  packaged, `../GxMcp.Gateway/bin/Debug/net8.0-windows`, `../../publish`. The dev-bin paths
  leak into packaged resolution logic.

The packaged extension should resolve its backend from the packaged `backend/` (which
`package.json` `files` ships) deterministically, and only consult dev-tree paths when it is
actually running from the dev checkout — never guess a repo root that isn't there.

## Current state

- `src/nexus-ide/src/gxShadowService.ts:76-79` — the `.git`/`.sln` walk-up workspace-root guess.
  Read the full surrounding method to see when it's hit (no open workspace).
- `src/nexus-ide/src/managers/BackendManager.ts:270-296` — the three-location gateway resolution.
  Read the full `resolveBackendDirectory`/`resolveLaunchSpec` (051's tests cover these — see
  `src/nexus-ide/src/test/suite/backendManager.test.ts` for the current asserted behavior; update
  those tests intentionally if you change resolution).
- `package.json` `files` ships `backend/` — the packaged extension's backend lives there,
  relative to the extension install dir (`context.extensionPath`).
- `extension.ts` has `context.extensionPath`/`context.extensionUri` available to anchor
  packaged resolution (grep for how BackendManager receives context).

## Commands you will need

Run from `src/nexus-ide`:

| Purpose | Command | Expected |
|---------|---------|----------|
| Compile | `npm run compile` | exit 0 |
| Gate | `npm run check` | compile+lint+test green (051's backendManager tests included) |

## Scope

**In scope**:
- `src/nexus-ide/src/managers/BackendManager.ts` (deterministic packaged-first resolution)
- `src/nexus-ide/src/gxShadowService.ts` (the workspace-root guess)
- `src/nexus-ide/src/test/suite/backendManager.test.ts` (update/extend to assert packaged vs dev resolution)

**Out of scope**:
- The gateway spawn/lease mechanics beyond path resolution.
- Changing how a dev F5 debug session resolves (must keep working) — only stop dev paths from
  being the packaged path, don't remove dev support.

## Steps

### Step 1: Anchor packaged resolution to `context.extensionPath`

In `BackendManager`, make the FIRST, authoritative resolution the packaged `backend/` under the
extension install dir (`context.extensionPath`/`extensionUri`). Only fall back to the dev-tree
bin/publish paths when a reliable dev-mode signal is present (e.g. `context.extensionMode ===
vscode.ExtensionMode.Development`, or the packaged backend is absent AND a dev marker exists).
Never let a dev path win in a packaged install.

**Verify**: `npm run compile` exit 0. Update `backendManager.test.ts` to assert: in a
"packaged" context (extensionMode=Production, backend/ present) resolution picks the packaged
path; in a "dev" context it may use the dev bin. Tests pass.

### Step 2: Make the workspace-root guess dev-only (or removed)

In `gxShadowService.ts:76-79`, gate the `.git`/`.sln` walk-up behind a dev-mode check (or remove
it if it's only ever needled during dev with no open workspace). In a packaged install with no
open workspace, it must NOT invent a repo root — fail cleanly / prompt "Open KB" instead
(the extension already has an `openKb` flow + a viewsWelcome prompting it).

**Verify**: `npm run compile` exit 0. Add/extend a test for the no-workspace path if feasible
(assert it does NOT return a fabricated repo root in production mode).

### Step 3: Gate

**Verify**: `npm run check` → all green (state counts). Do NOT break the 051 backendManager tests
except where you intentionally updated them (explain the diff).

## Done criteria

- [ ] Packaged resolution anchors to `context.extensionPath` packaged `backend/` first; dev paths only under a dev-mode signal.
- [ ] The `.git`/`.sln` workspace-root guess no longer runs in a packaged/production context.
- [ ] `backendManager.test.ts` asserts packaged-vs-dev resolution; `npm run check` green.
- [ ] Dev F5 debug still resolves the backend (reason about it / note the manual check).
- [ ] No files outside scope; `plans/README.md` updated.

## STOP conditions

- `context.extensionMode` / a reliable packaged-vs-dev signal isn't threaded to these call sites
  and wiring it is a big refactor — report; a smaller "packaged backend exists? use it, else dev"
  heuristic may suffice, but confirm it doesn't regress dev F5.
- Removing the `.sln` walk-up breaks the dev-no-workspace flow that the F5 debug relies on — keep
  it dev-gated rather than removed, and note it.

## Maintenance notes

- Reviewer: the risk is regressing the dev F5 loop OR the packaged install's backend discovery —
  both must work. Confirm the dev-mode gate is correct (not inverted) and packaged install finds `backend/`.
- This pairs with roadmap Phase 4 (folding VSIX packaging into `release.ps1`): deterministic
  packaged resolution is a prerequisite for a clean packaged release.
