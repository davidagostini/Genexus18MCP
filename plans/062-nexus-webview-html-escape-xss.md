# Plan 062: Escape KB-derived strings before HTML interpolation in Nexus IDE webviews (close stored-XSS sinks)

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 98b9a7d..HEAD -- src/nexus-ide/src/webviews/StructureView.ts src/nexus-ide/src/webviews/IndexView.ts src/nexus-ide/src/webviews/HistoryView.ts src/nexus-ide/src/utils`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1
- **Effort**: S-M
- **Risk**: LOW
- **Depends on**: none
- **Category**: security
- **Planned at**: commit `98b9a7d`, 2026-07-23

## Why this matters

Three Nexus IDE webviews build HTML by string-concatenating **KB-derived values**
(object/attribute/index names, descriptions, formulas, commit comments, error
strings) straight into `element.innerHTML` / `panel.webview.html`, with **no HTML
escaping**. Their Content-Security-Policy allows `script-src ... 'unsafe-inline'`,
so an injected inline event-handler attribute (e.g. a name field containing an
`onerror`/`onload` attribute payload) executes JavaScript in the webview when a
developer merely opens the Structure / Index / History view for that object. The
webview can `postMessage` back to the extension host to trigger privileged actions
(`save`, `setProperty`, `viewDiff`), so this escalates from "view metadata" to code
running in the webview context. KB content is attacker-influenceable (another
developer on a shared KB, a crafted import, a malicious object name).

The fix is the standard one and a working reference already exists in this repo:
`webviews/PropertiesView.ts` builds its rows with `document.createElement` +
`.innerText` and never interpolates data into an HTML string. This plan escapes
the string-concatenated sinks (the lower-risk, surgical fix) rather than rewriting
all three views to the DOM-API style.

## Current state

Files and their role:
- `src/nexus-ide/src/webviews/StructureView.ts` — visual-structure editor webview; builds table rows in `renderStructure()` inside an inline `<script>`.
- `src/nexus-ide/src/webviews/IndexView.ts` — index viewer webview; builds rows in `renderIndexes()` inside an inline `<script>`.
- `src/nexus-ide/src/webviews/HistoryView.ts` — revision-history webview; builds rows in `HistoryView.show()` on the **extension-host side** (TypeScript template literal), then injects into `getHtml()`.
- `src/nexus-ide/src/webviews/PropertiesView.ts` — **the correct reference**: uses `createElement` + `.innerText` (lines 88-120), no string interpolation of data.

### Sink 1 — StructureView (client-side, inside the inline `<script>`)

`renderStructure()` concatenates KB values directly into `html`, then
`document.getElementById('content').innerHTML = html` (line 299):

```js
// StructureView.ts:266
html += '<td class="item-name-cell">' + indentSpace + '<span class="icon ' + iconClass + '" onclick="toggleKey(\'' + id + '\')">' + iconHtml + '</span><span class="editable" ' + nameEditable + ' onblur="updateLocalData(\'' + id + '\', \'name\', this.innerText)">' + item.name + '</span></td>';
// StructureView.ts:269
html += '<td style="position: relative"><input type="text" list="gx-types" class="type-input" value="' + (item.type || '') + '" onchange="..." ' + typeDisabled + ' autocomplete="off"/></td>';
// StructureView.ts:272
html += '<td class="editable" contenteditable="' + descEditable + '" onblur="...">' + (item.description || '') + '</td>';
// StructureView.ts:275
html += '<td class="editable formula-text" contenteditable="' + formulaEditable + '" onblur="...">' + (item.formula || '') + '</td>';
```

`item.name`, `item.type`, `item.description`, `item.formula` come from the
`genexus_structure(get_visual)` result posted to the webview (`StructureView.ts:42`).
Also the extension-host error sinks (KB error text into HTML):

```ts
// StructureView.ts:44
panel.webview.html = `<h1>Error: ${result?.error || "Unknown error"}</h1>`;
// StructureView.ts:74
panel.webview.html = `<h1>Critical Error: ${e}</h1>`;
```

CSP (`StructureView.ts:83`): `... script-src ${cspSource} 'unsafe-inline';` — inline handlers run.

### Sink 2 — IndexView (client-side)

```js
// IndexView.ts:102 (error path)
document.getElementById('content').innerHTML = '<h2 ...>Error: ' + data.message + '</h2>';
// IndexView.ts:124
html += '<td class="index-name ' + rowClass + '">' + (idx.isPrimary ? icons.key : icons.file) + ' ' + idx.name + '</td>';
// IndexView.ts:128
html += '<li class="attr-item"><span>' + attr.name + '</span> <span class="order-tag">' + (attr.isAscending ? 'ASC' : 'DESC') + '</span></li>';
```

Sunk via `innerHTML` at `IndexView.ts:141`. `idx.name` / `attr.name` come from
`genexus_structure(get_indexes)`. CSP at `IndexView.ts:56` allows `'unsafe-inline'`.

### Sink 3 — HistoryView (extension-host side, TypeScript template literal)

```ts
// HistoryView.ts:58-66 — rows built on the host, interpolated into getHtml() at :73
(rev: any) => `
  <tr>
      <td ...>#${rev.version || rev.Id || ""}</td>
      <td ...>${rev.date || rev.Date || ""}</td>
      <td ...>${rev.user || rev.User || ""}</td>
      <td ...>${rev.comment || rev.Comment || '<span style="opacity: 0.5;">Sem comentario</span>'}</td>
      <td ...><button onclick="viewDiff(${rev.version || rev.Id})" ...>Comparar (Diff)</button></td>
  </tr>`
// HistoryView.ts:144 and :160 — objName interpolated into HTML text and into a JS string literal:
<h2>Historico de revisoes: ${objName} ...</h2>
function viewDiff(vId) { vscode.postMessage({ command: 'viewDiff', versionId: vId, objName: '${objName}' }); }
```

`rev.*` come from `genexus_history(list)` (`HistoryView.ts:47`). `objName` comes
from `GxUriParser.parse` (`HistoryView.ts:16`). CSP at `HistoryView.ts:133` allows `'unsafe-inline'`.

### Convention

TypeScript, 2-space indent, `tsc -p ./` compile, ESLint 9 flat config
(`src/nexus-ide/eslint.config.js`), tests under `src/nexus-ide/src/test/suite/*.test.ts`
run by `@vscode/test-electron` (Mocha). Existing webview test example to model:
`src/nexus-ide/src/test/suite/diagramView.test.ts` (asserts on the output of a
static `buildHtml`).

## Commands you will need

Run all from `src/nexus-ide/`.

| Purpose   | Command            | Expected on success        |
|-----------|--------------------|----------------------------|
| Compile   | `npm run compile`  | exit 0, no TS errors       |
| Lint      | `npm run lint`     | exit 0 (pre-existing warnings OK; **0 new errors**) |
| Tests     | `npm test`         | all pass, incl. new ones   |
| All gates | `npm run check`    | compile + lint + test all pass |

## Scope

**In scope** (the only files you may modify):
- `src/nexus-ide/src/utils/htmlEscape.ts` (create)
- `src/nexus-ide/src/webviews/StructureView.ts`
- `src/nexus-ide/src/webviews/IndexView.ts`
- `src/nexus-ide/src/webviews/HistoryView.ts`
- `src/nexus-ide/src/test/suite/htmlEscape.test.ts` (create)

**Out of scope** (do NOT touch):
- `src/nexus-ide/src/webviews/PropertiesView.ts` — already safe; it's the reference, leave it.
- `src/nexus-ide/src/webviews/DiagramView.ts` / `LayoutView.ts` — different CSP (nonce-only) tracked elsewhere.
- Removing `'unsafe-inline'` from the CSPs — a larger refactor (StructureView relies on many inline `onclick=` handlers). Escaping fully closes the injection; CSP hardening is a deferred follow-up (see Maintenance notes). Do NOT change any CSP `<meta>` in this plan.
- Any change to the `postMessage` protocol or MCP tool calls.

## Git workflow

- Branch: `advisor/062-nexus-webview-html-escape`
- Commit style: Conventional Commits (repo uses `fix(nexus-ide): ...`). Example from log: `fix(nexus-ide): CSP-harden webviews + vendor mermaid locally`.
- Do NOT push or open a PR unless the operator instructed it.

## Steps

### Step 1: Add a shared HTML-escape helper

Create `src/nexus-ide/src/utils/htmlEscape.ts`:

```ts
/**
 * Escapes a value for safe interpolation into HTML text or a double/single-quoted
 * attribute. Escaping all five of & < > " ' prevents both tag injection and
 * breaking out of an attribute to add an event-handler attribute. Non-string
 * inputs are coerced via String() (KB payloads are not guaranteed typed).
 */
export function escapeHtml(value: unknown): string {
  return String(value ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}
```

Note: `&` must be replaced first (or the entity prefixes get double-escaped).

**Verify**: `npm run compile` → exit 0.

### Step 2: Escape the extension-host (TypeScript) sinks

These are interpolated in `.ts` template literals, so they can call `escapeHtml` directly.

**HistoryView.ts** — `import { escapeHtml } from "../utils/htmlEscape";` then:
- Wrap each `rev.*` field in the row template (lines 60-63) with `escapeHtml(...)`. For the `comment` fallback keep the `<span>…</span>` default literal unescaped but escape only the value: `${rev.comment || rev.Comment ? escapeHtml(rev.comment || rev.Comment) : '<span style="opacity: 0.5;">Sem comentario</span>'}`.
- Line 65 `viewDiff(${rev.version || rev.Id})` — the version id flows into a JS call; coerce to a number so a non-numeric value can't inject: `viewDiff(${Number(rev.version ?? rev.Id) || 0})`. (Real GeneXus version ids are integers.)
- `getHtml()` line 144 `${objName}` and line 160 `objName: '${objName}'`: escape line 144 with `escapeHtml(objName)`; for line 160 (a JS single-quoted string literal), pass `objName` via the same numeric-safe route is wrong — instead JSON-encode it: `objName: ${JSON.stringify(objName)}`. `JSON.stringify` produces a safe quoted JS string literal.

**StructureView.ts** — `import { escapeHtml } from "../utils/htmlEscape";` then wrap the error sinks:
- Line 44: `` `<h1>Error: ${escapeHtml(result?.error || "Unknown error")}</h1>` ``
- Line 74: `` `<h1>Critical Error: ${escapeHtml(String(e))}</h1>` ``

**Verify**: `npm run compile` → exit 0.

### Step 3: Escape the client-side (inline-`<script>`) sinks

`renderStructure()` / `renderIndexes()` run **inside the webview**, so `escapeHtml`
(a module export) is not in scope there. Add a small escaper **inside each inline
`<script>`** and use it on every KB-derived interpolation. Add near the top of the
inline script (after `acquireVsCodeApi()`):

```js
function esc(v) {
  return String(v == null ? '' : v)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}
```

Then wrap the data interpolations (NOT the static markup / handler attributes that
reference `id`, which is an extension-generated index string, not KB data):
- **StructureView.ts**: `item.name` (line 266), `(item.type || '')` (line 269, inside the `value="..."` attribute), `(item.description || '')` (line 272), `(item.formula || '')` (line 275) → wrap each in `esc(...)`.
- **IndexView.ts**: `idx.name` (line 124), `attr.name` (line 128), and the error `data.message` (line 102) → wrap each in `esc(...)`.

Leave `id`, `iconHtml`/`icons.*` (static SVG constants), and boolean/enum-derived
strings (`iconClass`, `rowClass`, `ASC`/`DESC`, `typeDisabled`) unescaped — they are
extension-controlled, not KB data.

**Verify**: `npm run compile` → exit 0; `npm run lint` → 0 new errors.

### Step 4: Add regression tests for the helper

Create `src/nexus-ide/src/test/suite/htmlEscape.test.ts`, modeled structurally on
`src/nexus-ide/src/test/suite/diagramView.test.ts` (Mocha `suite`/`test`, `assert`).
Cover:
- `escapeHtml('<img src=x onerror=alert(1)>')` contains no raw `<` or `>` and contains `&lt;`.
- `escapeHtml('" onload="x')` contains no raw `"` (attribute-breakout case).
- `escapeHtml("' onclick='x")` contains no raw `'`.
- `escapeHtml('a & b')` → `a &amp; b` (ampersand escaped exactly once, not double).
- `escapeHtml(null)` → `''`; `escapeHtml(42)` → `'42'`.

**Verify**: `npm test` → all pass including the new tests.

### Step 5: Run the full gate

**Verify**: `npm run check` → compile + lint + test all pass.

## Test plan

- New file `htmlEscape.test.ts` covering the 6 cases in Step 4 (payload cases + null/number coercion + single-escape ampersand).
- Structural pattern: `src/nexus-ide/src/test/suite/diagramView.test.ts`.
- The client-side `esc()` in the inline scripts is not unit-testable via the harness (it lives in an HTML string); rely on the shared `escapeHtml` tests + manual read that the same 5 replacements are present. Do not add a DOM/browser test harness for this.
- Verification: `npm test` → all pass, N new tests in `htmlEscape.test.ts`.

## Done criteria

ALL must hold:
- [ ] `npm run compile` exits 0.
- [ ] `npm run lint` exits 0 with no NEW errors (pre-existing warnings acceptable).
- [ ] `npm test` passes; `htmlEscape.test.ts` exists with ≥6 passing assertions.
- [ ] `src/nexus-ide/src/utils/htmlEscape.ts` exists and is imported by `StructureView.ts` and `HistoryView.ts`.
- [ ] Grep shows no un-escaped KB-data interpolation left in the three files' data paths:
      `grep -n "+ item\.\(name\|type\|description\|formula\)" src/nexus-ide/src/webviews/StructureView.ts` returns **no matches** (all now `esc(item.…)`); likewise `grep -n "+ idx.name\|+ attr.name" src/nexus-ide/src/webviews/IndexView.ts` returns no matches.
- [ ] No files outside the in-scope list modified (`git status`).
- [ ] `plans/README.md` status row updated.

## STOP conditions

Stop and report (do not improvise) if:
- The "Current state" excerpts don't match live code (drift since `98b9a7d`).
- Escaping breaks a webview test that renders expected markup (e.g. a test asserting a literal `<` that was legitimate static markup) — report which test and the conflict.
- You find a KB-derived value flowing into a sink NOT listed here (e.g. a new field) — report it rather than silently escaping beyond scope; it may indicate the finding is broader.
- Any step's verification fails twice after a reasonable fix attempt.

## Maintenance notes

- **Deferred follow-up (do not do here)**: drop `'unsafe-inline'` from the `script-src`
  of these three CSPs and move inline `<script>` blocks to nonce'd scripts +
  `addEventListener` for the `onclick`/`onblur`/`onchange` handlers (mirroring
  `DiagramView.ts`'s nonce pattern). That is defense-in-depth on top of this
  escaping fix, larger, and MED-risk (StructureView has many inline handlers).
- Reviewer should scrutinize: that **every** KB-derived interpolation is escaped
  (a missed one re-opens the hole) and that no static/extension-controlled markup
  was over-escaped (which would render entities as visible text).
- If a future webview is added, it must follow either the `PropertiesView.ts`
  DOM-API pattern or this `esc()`/`escapeHtml` pattern — never raw interpolation.
