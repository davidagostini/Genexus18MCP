# Plan 067: Two nexus-ide correctness papercuts — guard `variable.type` + honor `includeDeclaration`

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 98b9a7d..HEAD -- src/nexus-ide/src/gxMemberResolver.ts src/nexus-ide/src/referenceProvider.ts`
> If either changed since this plan was written, compare the "Current state"
> excerpts against live code before proceeding; on a mismatch, STOP.

## Status

- **Priority**: P3
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none (independent; if 063 is executed first, `gxMemberResolver.ts` will differ elsewhere but not at the lines this plan touches)
- **Category**: bug
- **Planned at**: commit `98b9a7d`, 2026-07-23

## Why this matters

Two small, independent correctness defects in recently-rewritten providers:

1. **Unguarded `variable.type`** — `resolveVariableMembers` calls
   `variable.type.endsWith("Collection")` with no check that `type` is a string. A
   backend variable payload missing `type` throws a `TypeError`. Today it's masked by
   caller `try/catch` (the user just sees "no completions"), but it hides the real
   cause and any future caller without a wrapping catch crashes the provider.

2. **`ReferenceProvider` ignores `includeDeclaration`** — `provideReferences` accepts
   `context: vscode.ReferenceContext` but never reads `context.includeDeclaration`.
   VS Code callers that request references *without* the declaration (e.g. some
   rename-preview paths) still get it, violating the provider contract.

Both are S/LOW and touch disjoint symbols, so they're bundled into one plan.

## Current state

### Papercut 1 — `gxMemberResolver.ts:74-75`

```ts
let type = variable.type;                          // no type check
const isCollection = type.endsWith("Collection");  // throws if type is undefined
if (isCollection) type = "Collection";
```

`variable` comes from `variables.find(...)` over `provider.readObjectVariables`
output (`gxMemberResolver.ts:68-72`). The function's own docstring
(`gxMemberResolver.ts:58-59`) says it "Returns undefined when the variable itself is
unknown — callers must treat that as 'no suggestion'." A malformed variable with no
`type` should follow the same "unknown" contract, not throw.

### Papercut 2 — `referenceProvider.ts:19-24`

```ts
async provideReferences(
  document: vscode.TextDocument,
  position: vscode.Position,
  context: vscode.ReferenceContext,   // <-- accepted but never read
  _token: vscode.CancellationToken,
): Promise<vscode.Location[]> {
```

`context.includeDeclaration` is never referenced anywhere in the file. For the
**variable** path, the declaration site is a `&varName` occurrence within the current
document (found by `findVariableReferencesInDocument`, lines 77-93). For the
**attribute** path, references come from a KB `usedby:` query with no single
"declaration" location the provider knows — so honoring the flag is only meaningfully
actionable for the variable path (and for the current-document declaration line).

### Convention

TypeScript, 2-space indent (`gxMemberResolver.ts`) / this file uses 2-space
(`referenceProvider.ts`), `tsc -p ./`, ESLint 9. Tests:
`src/nexus-ide/src/test/suite/gxMemberResolver.test.ts` (create if absent — 063 may
create it; if it exists, extend it) and `referenceProvider.test.ts` (exists — extend).

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
- `src/nexus-ide/src/gxMemberResolver.ts` (papercut 1 only — the `variable.type` guard).
- `src/nexus-ide/src/referenceProvider.ts` (papercut 2 only — honor `includeDeclaration`).
- `src/nexus-ide/src/test/suite/gxMemberResolver.test.ts` (create or extend).
- `src/nexus-ide/src/test/suite/referenceProvider.test.ts` (extend).

**Out of scope**:
- The caching logic in `gxMemberResolver.ts` (that's plan 063 — don't touch `getObjectVariables`).
- The attribute `usedby:` path in `referenceProvider.ts` — don't attempt to synthesize an attribute "declaration" location; the flag is only applied to the variable path (see Step 2).
- `isVariableToken` / `GxVariableToken.ts`.

## Git workflow

- Branch: `advisor/067-nexus-resolver-reference-papercuts`
- Commit style: `fix(nexus-ide): ...`. Consider one commit per papercut for a clean review.
- Do NOT push or open a PR unless instructed.

## Steps

### Step 1: Guard `variable.type` in `resolveVariableMembers`

At `gxMemberResolver.ts:74`, before using `type`, return the "unknown" sentinel when
`type` isn't a usable string:

```ts
const type0 = variable.type;
if (typeof type0 !== "string" || type0.length === 0) return undefined;
let type = type0;
const isCollection = type.endsWith("Collection");
if (isCollection) type = "Collection";
```

This matches the function's documented "return undefined when unknown" contract.

**Verify**: `npm run compile` → exit 0.

### Step 2: Honor `includeDeclaration` for the variable path in `referenceProvider.ts`

For the **variable** branch (`referenceProvider.ts:36-38`), when
`context.includeDeclaration === false`, exclude the declaration occurrence. The
declaration for a GeneXus variable in a single-document scan is not separately marked,
so use the pragmatic, correct-for-VS-Code definition: the **first** `&varName`
occurrence in document order is treated as the declaration. Change
`findVariableReferencesInDocument` to accept the flag and drop the first match when
the declaration is excluded:

```ts
if (isVariable) {
  return this.findVariableReferencesInDocument(
    document, stripVariablePrefix(word), context.includeDeclaration,
  );
}
```

and in the method:

```ts
private findVariableReferencesInDocument(
  document: vscode.TextDocument,
  varName: string,
  includeDeclaration: boolean,
): vscode.Location[] {
  // ... build `locations` as today ...
  if (!includeDeclaration && locations.length > 0) {
    return locations.slice(1); // drop the first occurrence (treated as the declaration)
  }
  return locations;
}
```

Leave the attribute `usedby:` path unchanged (add a one-line comment there noting the
flag is not actionable for cross-KB attribute references, which have no single
provider-known declaration site).

**Verify**: `npm run compile` → exit 0; `npm run lint` → the `context` param is now read (no unused-var concern).

### Step 3: Tests

**`gxMemberResolver.test.ts`** (create/extend): add a case where a variable's `type`
is `undefined` → `resolveVariableMembers` returns `undefined` (no throw). Use a fake
`provider` whose `readObjectVariables` returns `[{ name: 'x' }]` (no `type`).

**`referenceProvider.test.ts`** (extend): with a document containing multiple `&myVar`
occurrences, assert:
- `includeDeclaration: true` → all occurrences returned.
- `includeDeclaration: false` → first occurrence dropped, count = N-1.
- Empty document / zero occurrences → `[]` in both modes (no negative slice).

Model on the existing cases in `referenceProvider.test.ts`.

**Verify**: `npm test` → all pass including new cases.

### Step 4: Full gate

**Verify**: `npm run check` → all pass.

## Test plan

- `gxMemberResolver.test.ts`: variable-missing-type → undefined (no throw).
- `referenceProvider.test.ts`: includeDeclaration true→all, false→drops-first, empty→[].
- Pattern: existing cases in `referenceProvider.test.ts`.
- Verification: `npm test` → all pass.

## Done criteria

ALL must hold:
- [ ] `npm run compile` exits 0.
- [ ] `npm run lint` exits 0, no new errors (the previously-unused `context` param is now read).
- [ ] `npm test` passes; new cases for both papercuts present and passing.
- [ ] `grep -n "typeof" src/nexus-ide/src/gxMemberResolver.ts` shows the `variable.type` guard.
- [ ] `grep -n "includeDeclaration" src/nexus-ide/src/referenceProvider.ts` shows the flag is now read.
- [ ] Only the in-scope files modified (`git status`).
- [ ] `plans/README.md` status row updated.

## STOP conditions

- "Current state" excerpts don't match live code (drift) — in particular, if plan 063
  already reshaped `getObjectVariables`, confirm the `resolveVariableMembers` lines
  74-75 are still as excerpted before editing.
- The "first occurrence = declaration" heuristic turns out to be wrong for how the
  repo defines a variable declaration (e.g. a separate Variables part) — if a test
  reveals the declaration isn't the first source occurrence, STOP and report; do not
  invent a more elaborate declaration-finder in this papercut plan.
- A step verification fails twice after a reasonable fix.

## Maintenance notes

- Papercut 2's "first occurrence = declaration" is a pragmatic single-document
  heuristic. If cross-object variable references or a real declaration-locator are
  added later, revisit this to point at the true declaration site.
- Reviewer should confirm the two changes are independent and neither touches the
  caching path (owned by plan 063) or the attribute `usedby:` path.
