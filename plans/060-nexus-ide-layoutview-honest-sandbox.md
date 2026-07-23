# Plan 060: LayoutView — honest read-only label + sandboxed rendering (Phase 3)

> **Executor instructions**: Follow step by step, verify each, STOP-and-report on STOP
> conditions, update `plans/README.md` when done. Minimal narration; final report in one message.
>
> **Drift check (run first)**: `git diff --stat f9dd16c..HEAD -- src/nexus-ide/src/webviews/LayoutView.ts`

## Status

- **Priority**: P3
- **Effort**: S-M
- **Risk**: LOW-MED (changes how untrusted SDK HTML is rendered)
- **Depends on**: 051 (test baseline)
- **Category**: feature / security (honesty + hardening)
- **Planned at**: commit `f9dd16c`, 2026-07-23
- **Roadmap**: `docs/nexus-ide-roadmap.md` Phase 3

## Why this matters

`LayoutView` presents itself like the other part views but is a **passthrough**: it sets
`panel.webview.html` directly to `result.source` — a full HTML document rendered by the GeneXus
SDK, in a scripts-enabled webview with no CSP (plan 056 deliberately punted on CSP'ing it
because the SDK HTML shape is unknown). Two gaps: (1) it reads as an editor but is view-only,
and (2) arbitrary SDK-authored HTML runs unconstrained in the webview context. This plan is the
right-sized fix for quality parity — NOT a visual layout editor (that's a separate product
effort). Make it honestly a **read-only preview** and **contain** the untrusted HTML in a
sandboxed iframe so the outer webview can carry a strict CSP.

## Current state

- `src/nexus-ide/src/webviews/LayoutView.ts` — the whole file (~55 lines). `show()` creates a
  panel titled `` `${objName} Layout` `` with `{ enableScripts: true, enableCommandUris: true,
  localResourceRoots: [] }` (line 27-32), sets `panel.webview.html = result.source` (line 45)
  from `readMcpResource(genexus://objects/<target>/part/Layout)`. Carries a plan-056 comment
  (lines 21-26) explaining why it's un-CSP'd.
- Contrast: the other webviews (post-056) host extension-authored HTML with a strict nonce CSP.
  LayoutView is different because its body is SDK-authored, unknown-shape HTML.
- `Logger`, `check` gate, `@vscode/test-electron` (baseline 62 tests).

## Commands you will need

Run from `src/nexus-ide`: `npm run compile`, `npm run lint`, `npm run check`.

## Scope

**In scope**:
- `src/nexus-ide/src/webviews/LayoutView.ts`
- `src/nexus-ide/src/test/**`

**Out of scope**:
- Building a visual/editable layout designer (explicitly NOT this plan — record as a possible future direction).
- Changing the MCP `part/Layout` resource or the SDK HTML it returns.

## Steps

### Step 1: Honest read-only labeling

Rename the panel title / add a header banner making clear this is a **read-only preview** (e.g.
title `` `${objName} Layout (read-only)` `` and/or a small "Read-only preview — edit layout in the
GeneXus IDE" note). No functional change, just truth in labeling.

**Verify**: `npm run compile` exit 0.

### Step 2: Contain the SDK HTML in a sandboxed iframe under a strict outer CSP

Restructure `show()` so the OUTER webview HTML is extension-authored (now CSP-able): a strict
nonce CSP + a single `<iframe>` whose `srcdoc` carries the SDK `result.source`, with a `sandbox`
attribute that isolates it from the extension/webview context (start restrictive; if the SDK
layout needs its own scripts to render, `sandbox="allow-scripts"` still blocks it from reaching
the parent `acquireVsCodeApi`/`vscode-resource` origin — the containment win — while letting its
own inline scripts run). Set the outer webview's CSP to allow the iframe (`frame-src` / the
srcdoc) and nothing remote. Drop `enableCommandUris` unless something needs it. Keep the
loading/error states.

**Verify**: `npm run compile` exit 0. Test asserts the generated OUTER html contains a CSP meta,
wraps `result.source` inside an `<iframe` with a `sandbox` attribute, and does not set
`panel.webview.html` to raw `result.source` directly.

### Step 3: Gate

**Verify**: `npm run check` → all green (state counts). If the electron host can render the
webview, a smoke check that the iframe mounts is ideal; otherwise assert on the generated HTML string.

## Done criteria

- [ ] LayoutView is labeled a read-only preview (no longer implies an editor).
- [ ] SDK HTML is rendered inside a `sandbox`ed iframe; the outer webview carries a strict CSP; `webview.html` is no longer set to raw `result.source`.
- [ ] Test asserts CSP + sandboxed-iframe wrapping.
- [ ] `npm run check` green; `plans/README.md` updated; the 056 follow-up comment resolved/updated.

## STOP conditions

- Drift on the file.
- Without a live KB you cannot confirm the SDK layout still renders inside a sandboxed iframe and
  a chosen `sandbox` level breaks it — ship Step 1 (honest labeling) + the outer strict CSP with
  the SDK HTML in an iframe at the MOST permissive sandbox that still isolates the parent origin,
  and record "verify sandbox level against a real layout" as a follow-up. Do NOT ship something
  that clearly can't render.

## Maintenance notes

- Reviewer: the win is (a) honesty (read-only) and (b) the untrusted SDK HTML no longer shares
  the extension webview's origin/capabilities. Confirm the iframe sandbox actually isolates it.
- Possible future direction (not this plan): a real editable layout view if/when the MCP grows a
  richer layout write API.
