# Plan 056: Harden webviews — CSP + local mermaid (Phase 2)

> **Executor instructions**: Follow step by step, verify each, STOP-and-report on STOP
> conditions, update `plans/README.md` when done. Minimal narration; final report in one message.
>
> **Drift check (run first)**: `git diff --stat f98ecd0..HEAD -- src/nexus-ide/src/webviews`

## Status

- **Priority**: P2
- **Effort**: S-M
- **Risk**: LOW-MED (webview CSP can break rendering if misconfigured — verify visually/in test)
- **Depends on**: none
- **Category**: security / robustness
- **Planned at**: commit `f98ecd0`, 2026-07-23
- **Roadmap**: `docs/nexus-ide-roadmap.md` Phase 2

## Why this matters

`webviews/DiagramView.ts` opens a scripts-enabled webview (`enableScripts: true`, line 42)
with **no Content-Security-Policy and no `localResourceRoots`**, and pulls `mermaid` from a
public CDN (`https://cdn.jsdelivr.net/...`, line 61). That means: (1) the diagram only works
online, and (2) a scripts-enabled webview with no CSP loading remote script is a genuine
supply-chain / injection surface for a shipping IDE. Bundling mermaid locally + adding a
strict CSP fixes both. The other webviews (Structure/Properties/History/Index/Layout) should
be checked for the same gap.

## Current state

- `src/nexus-ide/src/webviews/DiagramView.ts:42` — `{ enableScripts: true }` (no
  `localResourceRoots`), `:61` `<script src="https://cdn.jsdelivr.net/npm/mermaid/dist/mermaid.min.js">`,
  `:62` `mermaid.initialize(...)`, `:65-66` injects `${result.mermaid}` (server-provided) into
  a `<pre class="mermaid">`.
- Other webviews under `src/nexus-ide/src/webviews/` (StructureView, PropertiesView,
  HistoryView, IndexView, LayoutView) — grep each for `enableScripts` + whether they set a CSP
  meta tag: `grep -rn "enableScripts\|Content-Security-Policy\|localResourceRoots\|<script" src/nexus-ide/src/webviews`.
- `package.json` `files` already ships `resources`, `syntaxes`, `themes` — a vendored asset
  under `resources/` (or `media/`) will be packaged.
- No `mermaid` npm dependency today (it's CDN-loaded). Test infra: `npm run check`, 56 tests.

## Commands you will need

Run from `src/nexus-ide`:

| Purpose | Command | Expected |
|---------|---------|----------|
| Compile | `npm run compile` | exit 0 |
| Gate | `npm run check` | compile+lint+test green |

## Scope

**In scope**:
- `src/nexus-ide/src/webviews/DiagramView.ts` (CSP + local mermaid + `localResourceRoots`)
- A vendored `mermaid.min.js` under `src/nexus-ide/resources/` (or `media/`) — committed asset,
  OR a `mermaid` devDependency copied to `out`/`resources` at compile (pick the simpler; a
  committed vendored file is fine and avoids a runtime dep).
- Other `src/nexus-ide/src/webviews/*.ts` ONLY to add a CSP meta + `localResourceRoots` where a
  scripts-enabled webview currently lacks one (minimal, no feature change).
- `src/nexus-ide/package.json` `files`/deps only if vendoring requires it.
- `src/nexus-ide/src/test/**` (a test asserting DiagramView HTML contains a CSP + no `https://` script src).

**Out of scope**:
- Changing what any webview DISPLAYS or its message protocol.
- Restyling; theme work.

## Steps

### Step 1: Vendor mermaid + CSP the DiagramView

Vendor `mermaid.min.js` into `resources/` (download the pinned version matching what the CDN
served, commit it). In `DiagramView.ts`: set `localResourceRoots` to the resources dir, load
mermaid via `webview.asWebviewUri(...)` instead of the CDN URL, and add a strict CSP `<meta>`
(default-src none; script-src the webview cspSource + a per-render nonce; style-src as needed).
Keep the server-provided `${result.mermaid}` injected as diagram TEXT inside `<pre>` (it's
mermaid source, not HTML) — ensure it can't break out (it already sits in a `<pre>`; confirm no
HTML-injection via the CSP + escaping if needed).

**Verify**: `npm run compile` exit 0. A test asserts the generated HTML contains a
`Content-Security-Policy` meta and does NOT contain `https://cdn.` / an external `script src`.

### Step 2: Sweep the other webviews

For each scripts-enabled webview lacking a CSP, add a minimal CSP meta + `localResourceRoots`.
If a webview isn't scripts-enabled or has no dynamic content, leave it (note it). Do NOT change
their behavior.

**Verify**: `grep -rn "enableScripts: true" src/nexus-ide/src/webviews` cross-checked — every
scripts-enabled webview now emits a CSP meta (assert in a test or list them in notes). `npm run compile` exit 0.

### Step 3: Gate

**Verify**: `npm run check` → all green (state counts). If the environment can render the
webview in the electron test host, a smoke assertion that mermaid loads from the local URI is
ideal; otherwise assert on the generated HTML string (CSP present, no remote src).

## Done criteria

- [ ] DiagramView loads mermaid from a local vendored asset (no `cdn.jsdelivr.net`), under a strict CSP with `localResourceRoots` set.
- [ ] Every scripts-enabled webview emits a CSP meta (list them).
- [ ] Test asserts DiagramView HTML has CSP + no external script src.
- [ ] `npm run check` green; no display/behavior change to any webview; `plans/README.md` updated.

## STOP conditions

- Adding CSP breaks a webview's rendering in a way you can't resolve within the plan (e.g. an
  inline handler that needs a nonce refactor bigger than expected) — ship the DiagramView fix,
  and record the stubborn webview as a scoped follow-up.
- The exact mermaid version the code depends on can't be determined — pin a current stable
  mermaid and note the version chosen.

## Maintenance notes

- Reviewer: confirm no `https://`/`http://` script or style src remains in any webview, and
  that the DiagramView still renders (local asset + CSP nonce wired correctly).
- Vendored mermaid needs occasional version bumps — note the pinned version + source URL in a comment.
