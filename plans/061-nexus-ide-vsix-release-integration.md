# Plan 061: Fold the Nexus IDE VSIX into the release flow (Phase 4)

> **Executor instructions**: Follow step by step. This modifies the release mechanism —
> verify with `-DryRun` and do NOT cut a real release from this plan. STOP-and-report on STOP
> conditions. Update `plans/README.md` when done. Minimal narration; final report in one message.
>
> **Drift check (run first)**: `git diff --stat <planned-at SHA>..HEAD -- release.ps1 src/nexus-ide/package.json`

## Status

- **Priority**: P3
- **Effort**: M
- **Risk**: MED-HIGH (touches `release.ps1`, the production release mechanism)
- **Depends on**: 051 (extension must compile/package cleanly)
- **Category**: dx / release
- **Planned at**: commit `db72a20`, 2026-07-23
- **Roadmap**: `docs/nexus-ide-roadmap.md` Phase 4

## Why this matters

Decision (2026-07-23): the Nexus IDE extension ships **in lockstep with the MCP** — one
release cycle. Today `release.ps1` bumps + builds + publishes only the MCP server
(`genexus-mcp` npm package + `publish.zip` GitHub asset); the extension (`src/nexus-ide`, its
own `package.json` at `1.1.0`) is never built, versioned, or published by the release. This
plan makes an MCP release also produce the extension **VSIX**, version it in lockstep, and
attach it to the same GitHub Release — so "cut a release" ships both.

## Current state

- `release.ps1` (repo root) — the one-shot release. Key sections: version resolve + tree check
  (`~:64-147`), version bump of `package.json` + both csprojs (`~:149-224`), `build.ps1`
  (`~:226`), tests (`~:257`), zip `publish/` → `publish.zip` (+ `.sha256`) (`~:303`), commit +
  tag + push (`~:323-345`), `gh release create <tag> --target main $zipPath [$shaPath]`
  (`~:368-386`), workflow dispatch (`~:397`). It uses `Invoke-Cmd` (note the `$Arguments`
  naming caveat at `:47-62`) and honors `-DryRun`/`-SkipBuild`/`-SkipTests`.
- The bump block edits `package.json`, `GxMcp.Gateway.csproj`, `GxMcp.Worker.csproj`. The
  extension `src/nexus-ide/package.json` (`"version": "1.1.0"`) is NOT touched.
- `.github/workflows/release.yml` — publishes `genexus-mcp` to npm from `publish.zip` on the
  release. (Read it to see if it should also do anything with a VSIX — likely NOT; VSIX
  marketplace publish needs a `VSCE_PAT` secret, out of scope unless one exists.)
- The extension builds a VSIX via `@vscode/vsce` (`npx @vscode/vsce package`); `vsce` is not a
  current devDependency (invoke via `npx`). Its `package.json` `files` array is already set for packaging.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Compile ext | `npm --prefix src/nexus-ide run compile` | exit 0 |
| Package VSIX (rehearse) | `cd src/nexus-ide; npx @vscode/vsce package --no-dependencies` | produces `nexus-ide-<v>.vsix` |
| Release dry-run | `pwsh -File release.ps1 -Version <X.Y.Z> -DryRun` | rehearses, no origin changes |

(Run `release.ps1` via pwsh 7 — see repo memory; PS 5.1 chokes on its em-dashes.)

## Scope

**In scope**:
- `release.ps1` — extend the bump block to also set `src/nexus-ide/package.json` version to
  `$Version`; add a step that compiles + packages the VSIX; add the `.vsix` to the
  `gh release create` positional assets.
- `src/nexus-ide/package.json` (version field only, if a starting realignment is needed).
- `docs/nexus-ide-roadmap.md` (tick Phase 4).

**Out of scope**:
- Marketplace publish (`vsce publish`) — needs a `VSCE_PAT` secret. Do NOT hard-require it;
  if you add it at all, gate it behind an env var presence check (`$env:VSCE_PAT`) and skip
  cleanly when absent. Document the manual publish step instead.
- The `.github/workflows/release.yml` npm publish path (leave unless a VSIX-publish job is
  explicitly wanted AND a PAT secret exists — default: don't touch it).
- `build.ps1` (the MCP build) — the VSIX build is a separate, additive step.

## Steps

### Step 1: Version the extension in lockstep

In `release.ps1`'s bump block, add `src/nexus-ide/package.json` to the files whose `"version"`
is set to `$Version` (same regex-preserving-format approach used for the root `package.json` at
`~:182-190`). Also add it to the `$bumpFiles` dirty-tree allowlist (`~:89-96`) so a resumed
release doesn't choke on it.

**Verify**: `pwsh -File release.ps1 -Version 9.9.9 -DryRun` reaches the bump step and reports it
would set the extension version (dry-run makes no writes). Exit 0.

### Step 2: Build + package the VSIX

Add a step (after the MCP build, before/around the zip step) that: compiles the extension
(`npm --prefix src/nexus-ide install` if needed, then `run compile`) and packages it
(`npx @vscode/vsce package --no-dependencies -o <root>/nexus-ide-$Version.vsix` run from
`src/nexus-ide`). Guard it under `-not $SkipBuild`. On `-DryRun`, only echo the intended command
(like the existing `Invoke-Cmd` dry-run behavior). If `vsce` packaging fails, `Fail` clearly
(don't ship a release claiming an extension it couldn't build).

**Verify**: `cd src/nexus-ide; npx @vscode/vsce package --no-dependencies` produces a `.vsix`
locally (rehearse outside the script first to confirm the extension packages cleanly at all —
if it errors on missing `repository`/`license`/icon fields, fix those in the extension
`package.json`, which is in scope for a clean package).

### Step 3: Attach the VSIX to the GitHub release

Add the `.vsix` path to the `$createArgs` positional assets for `gh release create` (`~:377-385`),
alongside `publish.zip` + the sha sidecar, so it uploads in the same call.

**Verify**: `pwsh -File release.ps1 -Version 9.9.9 -DryRun` shows the `gh release create` line
including the `.vsix` asset path. Exit 0. NO real release.

### Step 4: Tick roadmap + document manual marketplace publish

Tick Phase 4 in `docs/nexus-ide-roadmap.md`. Add a short note (roadmap or extension README)
on the manual `vsce publish` step + that it needs a `VSCE_PAT` (not automated here).

## Done criteria

- [ ] `release.ps1 -DryRun` sets the extension version in lockstep, packages a VSIX (dry-echo), and lists the `.vsix` in the `gh release create` assets — all without touching origin.
- [ ] The extension packages cleanly (`npx @vscode/vsce package` succeeds) — any missing manifest fields fixed.
- [ ] Marketplace publish is NOT hard-wired (absent-PAT skips cleanly or is manual-only).
- [ ] `git status` clean except intended edits; `plans/README.md` + roadmap updated.
- [ ] **No real release cut by this plan** (dry-run only).

## STOP conditions

- `npx @vscode/vsce package` fails for a reason bigger than manifest-field fixes (e.g. it
  demands bundling that conflicts with the current `out/` layout) — report; attaching a
  pre-built VSIX may need a bundler step (esbuild) that's its own plan.
- The `release.ps1` bump-regex for the extension `package.json` risks matching the wrong
  `"version"` (e.g. a nested dep) — scope the replace to the first top-level `"version"` only,
  as the root-package bump does; if unsure, STOP.
- Editing `release.ps1` in a way you can't verify via `-DryRun` — do NOT guess against the live
  release path; report.

## Maintenance notes

- Reviewer: the release mechanism is production-critical — confirm `-DryRun` shows the full
  intended flow and that a real release still bumps/builds/zips/tags the MCP exactly as before
  (the VSIX steps are purely additive). The FIRST real release after this lands should be watched closely.
- If a `VSCE_PAT` is later added as a repo secret, a follow-up can automate marketplace publish
  in `release.yml`.
