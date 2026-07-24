# Plan 065: Contain gxkb18-URI-derived filesystem paths inside the shadow root (path-traversal defense)

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 98b9a7d..HEAD -- src/nexus-ide/src/utils/GxUriParser.ts src/nexus-ide/src/gxFileSystem.ts src/nexus-ide/src/gxShadowService.ts`
> If any changed since this plan was written, compare the "Current state"
> excerpts against live code before proceeding; on a mismatch, STOP.

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: MED
- **Depends on**: none
- **Category**: security
- **Planned at**: commit `98b9a7d`, 2026-07-23

## Why this matters

The `gxkb18://` FileSystem provider maps virtual URIs to real files under a shadow
root (`.gx_mirror`) using `path.join(shadowRoot, info.type, `${info.name}.gx`)`. The
`type`/`name` segments come from `GxUriParser.parse`, which for the `gxkb18` scheme
does **no `..`/absolute-path rejection**. `path.join` does not stop `..` from escaping
its base (`path.join('/root','..','x')` resolves outside `/root`), so a crafted URI or
a KB/gateway response whose `type`/`name` contains traversal segments can drive
`stat`/`readFile`/**`writeFile`** outside the shadow root — including overwriting
arbitrary files via the write path (`syncToDisk` → `fs.writeFileSync`/`renameSync`).

Note the asymmetry that makes this a clear defect: the **`file:`-scheme** branch of
the same parser (`parseFileUri`) already enforces containment
(`GxUriParser.ts:122` — `startsWith(_shadowRoot)`), but the `gxkb18` branch does not.
This plan closes that gap with a shared containment helper, matching the protection
the `file:` path already has.

## Current state

Files:
- `src/nexus-ide/src/utils/GxUriParser.ts` — URI parsing. The `gxkb18` branch (`parse`, lines 161-193) is unguarded; the `file:` branch (`parseFileUri`, lines 118-155) has a `startsWith` containment check at line 122.
- `src/nexus-ide/src/gxFileSystem.ts` — `FileSystemProvider`; builds a shadow path from parsed `info` (lines 297-302 shown below).
- `src/nexus-ide/src/gxShadowService.ts` — owns `shadowRoot`, builds many `path.join(this._shadowRoot, ...)` paths and writes files (`writeFileSync`/`renameSync` at lines 126-127; canonical path at 258; fallback dir at 563).

The unguarded gxkb18 parse (`GxUriParser.ts:168-193`):

```ts
const pathStr = decodeURIComponent(uri.path.substring(1));
const parts = pathStr.split("/");
const fileName = parts.pop() || "";
const type = parts.pop() || "";          // <-- no '..' / absolute rejection
let cleanName = fileName.replace(".gx", "");
// ... derives `name` from cleanName ...
return { type, name: cleanName, part, path: pathStr };
```

The `file:` branch, by contrast, DOES contain (`GxUriParser.ts:121-122`):

```ts
const normalizedFsPath = uri.fsPath.replace(/[\\/]+$/, "");
if (!normalizedFsPath.toLowerCase().startsWith(this._shadowRoot.toLowerCase())) return null;
```

The sink in `gxFileSystem.ts:294-303`:

```ts
if (size === 0) {
  const info = GxUriParser.parse(uri);
  if (info) {
    const shadowPath = path.join(
      this._shadowService?.shadowRoot || "",
      info.type,
      `${info.name}.gx`,
    );
    size = fs.existsSync(shadowPath) ? fs.statSync(shadowPath).size : 0;
  }
}
```

Legitimate `type`/`name` values: GeneXus object type folders (`Procedure`,
`Transaction`, `WebPanel`, `Table`, `SDT`, …) and object names. Module nesting can
put **multiple `/`-separated segments** in the mirror `path` (see
`gxShadowService.ts` `walk`/module dirs), so the containment check must operate on
the **final resolved absolute path**, not by forbidding all `/`.

### Convention

TypeScript, 2-space indent, `tsc -p ./`, ESLint 9. Tests:
`src/nexus-ide/src/test/suite/gxUriParser.test.ts` already exists — extend it.

## Commands you will need

Run from `src/nexus-ide/`.

| Purpose | Command           | Expected |
|---------|-------------------|----------|
| Compile | `npm run compile` | exit 0   |
| Lint    | `npm run lint`    | 0 new errors |
| Tests   | `npm test`        | all pass |
| Gate    | `npm run check`   | all pass |

## Scope

**In scope**:
- `src/nexus-ide/src/utils/GxUriParser.ts` — add segment sanitization + a shared `resolveWithinRoot` helper.
- `src/nexus-ide/src/gxFileSystem.ts` — route the shadow-path build through the helper.
- `src/nexus-ide/src/gxShadowService.ts` — route the URI-derived path builds (the ones fed by `info.type`/`info.name` or parsed URIs) through the helper.
- `src/nexus-ide/src/test/suite/gxUriParser.test.ts` (extend).

**Out of scope**:
- The `file:`-scheme `parseFileUri` containment — already correct; don't duplicate.
- `path.join(this._shadowRoot, INDEX_FILE)` and similar **constant-filename** joins in `gxShadowService.ts` (lines 176, 180) — the trailing segment is an extension constant, not URI data. Leave them.
- Changing the gateway transport or the mirror-index format.

## Git workflow

- Branch: `advisor/065-nexus-path-containment`
- Commit style: `fix(nexus-ide): ...`.
- Do NOT push or open a PR unless instructed.

## Steps

### Step 1: Add a shared `resolveWithinRoot` helper to `GxUriParser`

Add a static method that resolves segments under a root and rejects escapes. Target shape:

```ts
import * as path from "path";  // already imported

/**
 * Joins `segments` under `root` and returns the absolute path ONLY if it stays
 * inside `root`; returns null on any traversal/absolute escape. Allows legitimate
 * nested segments (module folders) — containment is checked on the resolved path,
 * not by forbidding '/'.
 */
static resolveWithinRoot(root: string, ...segments: string[]): string | null {
  if (!root) return null;
  const resolvedRoot = path.resolve(root);
  const target = path.resolve(resolvedRoot, ...segments.filter((s) => s.length > 0));
  const rootWithSep = resolvedRoot.endsWith(path.sep) ? resolvedRoot : resolvedRoot + path.sep;
  if (target !== resolvedRoot && !target.startsWith(rootWithSep)) return null;
  return target;
}
```

Also harden the `gxkb18` `parse` branch: after computing `type`/`cleanName`, reject a
parse that contains traversal or absolute markers so a bad URI fails fast rather than
silently mis-resolving. Add before the `return { type, name: cleanName, ... }`:

```ts
if (this.hasUnsafeSegment(type) || this.hasUnsafeSegment(cleanName)) return null;
```

with:

```ts
private static hasUnsafeSegment(seg: string): boolean {
  if (!seg) return false;
  return seg.split(/[\\/]/).some((p) => p === ".." || p === "." ) ||
    /\0/.test(seg) || path.isAbsolute(seg);
}
```

**Verify**: `npm run compile` → exit 0.

### Step 2: Route the `gxFileSystem.ts` shadow-path build through the helper

Replace the raw `path.join` at `gxFileSystem.ts:297-301` with:

```ts
const shadowRoot = this._shadowService?.shadowRoot || "";
const shadowPath = GxUriParser.resolveWithinRoot(shadowRoot, info.type, `${info.name}.gx`);
size = shadowPath && fs.existsSync(shadowPath) ? fs.statSync(shadowPath).size : 0;
```

(When `resolveWithinRoot` returns null — escape attempt — treat as not-found: `size = 0`.)

**Verify**: `npm run compile` → exit 0.

### Step 3: Route the URI-derived builds in `gxShadowService.ts`

Find every `path.join` in `gxShadowService.ts` whose trailing segment(s) derive from
`info.type`/`info.name`, a parsed URI, or a gateway object's `type`/`name` (the audit
flagged the canonical path at line 258 and the fallback dir at line 563). For each,
resolve via `GxUriParser.resolveWithinRoot(this._shadowRoot, ...)` and, if it returns
null, **abort that operation** (do not read/write) — log via `Logger.warn` with the
offending value and return the function's existing "not found"/no-op result. Do **not**
convert constant-filename joins (index files, lines 176/180).

Before editing, list the candidate sites so the change is deliberate:
`grep -n "path.join" src/nexus-ide/src/gxShadowService.ts` — for each, decide
"URI/object-derived → guard" vs "constant/relative-index → leave". Record the decision
in the commit message.

**Verify**: `npm run compile` → exit 0; `npm test` → existing shadow-service tests still pass.

### Step 4: Add tests

Extend `src/nexus-ide/src/test/suite/gxUriParser.test.ts`. Cover:
- `resolveWithinRoot(root, 'Procedure', 'MyProc.gx')` → an absolute path that `startsWith(root)`.
- `resolveWithinRoot(root, 'Module1', 'Module2', 'X.gx')` → **allowed** (legitimate nesting), stays under root.
- `resolveWithinRoot(root, '..', '..', 'X.gx')` → `null` (escape rejected).
- `resolveWithinRoot(root, 'C:\\\\Windows\\\\System32', 'x')` (absolute segment) → `null`.
- `GxUriParser.parse(vscode.Uri.parse('gxkb18:/../../Type/Name.gx'))` → `null` (unsafe parse rejected).
- A normal `gxkb18:/Procedure/MyProc.gx` still parses to `{ type: 'Procedure', name: 'MyProc', ... }` (no regression).

**Verify**: `npm test` → all pass including the new cases.

### Step 5: Full gate

**Verify**: `npm run check` → all pass.

## Test plan

- New cases in `gxUriParser.test.ts`: allowed-simple, allowed-nested, reject-traversal, reject-absolute, reject-unsafe-parse, normal-parse-unchanged.
- Pattern: existing cases in `gxUriParser.test.ts`.
- Verification: `npm test` → all pass. If the harness has a shadow-service test that mounts a real temp dir, confirm it still round-trips a normal object (no false rejection of nested modules).

## Done criteria

ALL must hold:
- [ ] `npm run compile` exits 0.
- [ ] `npm run lint` exits 0, no new errors.
- [ ] `npm test` passes; new `gxUriParser.test.ts` cases (≥6) pass.
- [ ] `gxFileSystem.ts` shadow-path build goes through `resolveWithinRoot` (grep confirms; no raw `path.join(... info.type ...)` remains there).
- [ ] URI/object-derived `path.join` sites in `gxShadowService.ts` are guarded; constant-filename joins are intentionally left (documented in commit message).
- [ ] Only the four in-scope files modified (`git status`).
- [ ] `plans/README.md` status row updated.

## STOP conditions

- "Current state" excerpts don't match live code (drift).
- The containment check rejects a **legitimate** nested-module object during the
  shadow-service test round-trip (false positive) — STOP and report; the allow-nested
  case must pass. Do NOT loosen the check to "forbid `..` only" without confirming the
  resolved-path approach handles real KB module structures.
- You cannot cleanly classify a `path.join` site in `gxShadowService.ts` as
  URI-derived vs constant — STOP and report the ambiguous site rather than guessing.
- A step verification fails twice after a reasonable fix.

## Maintenance notes

- This is defense-in-depth: exploitability depends on whether the gateway ever returns
  attacker-influenced `type`/`name` and on VS Code's own URI validation. The
  asymmetry with the already-guarded `file:` path is the concrete justification —
  after this, both scheme branches enforce containment.
- Reviewer should scrutinize: no legitimate nested path is rejected, and every
  URI-derived write path (the dangerous direction) is covered, not just the `stat`
  read path.
- Follow-up deferred: consolidating the `file:`-branch containment to also call
  `resolveWithinRoot` (currently a bespoke `startsWith`) — cosmetic, left out to keep
  this change surgical.
