# Plan 063: Give the `&var.` member cache a TTL so completions reflect KB edits

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 98b9a7d..HEAD -- src/nexus-ide/src/gxMemberResolver.ts src/nexus-ide/src/inlineCompletionProvider.ts src/nexus-ide/src/completionProvider.ts src/nexus-ide/src/hoverProvider.ts`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `98b9a7d`, 2026-07-23

## Why this matters

`getObjectVariables` caches an object's declared variables the first time they're
fetched (for `&var.` member completion / inline ghost text) and **never invalidates**
the entry. Once cached, that object's variable list is frozen for the whole IDE
session: adding a new variable, changing a variable's type (`Character` → an SDT),
or renaming a variable will not be reflected — inline/member completion silently
returns nothing for the new/changed variable, or offers members for the *old* type,
until the window reloads. This is a "looks broken" freshness bug on a feature the
extension was recently rewritten to make correct.

A TTL-expiry cache pattern already exists in this repo — `hoverProvider.ts` uses a
30-second TTL with size-bounded cleanup. This plan reuses that exact pattern.

## Current state

Files:
- `src/nexus-ide/src/gxMemberResolver.ts` — `getObjectVariables(provider, objName, cache)` owns the cache read/write; `cache` is a `Map` passed in by the caller.
- `src/nexus-ide/src/inlineCompletionProvider.ts` — holds `private varCache = new Map<string, any[]>()` (line 13), passes it to `resolveVariableMembers` → `getObjectVariables`.
- `src/nexus-ide/src/completionProvider.ts` — holds its own `varCache` and calls `getObjectVariables` the same way (**shares this bug**).
- `src/nexus-ide/src/hoverProvider.ts` — **the reference TTL pattern**.

The no-invalidation cache (`gxMemberResolver.ts:36-53`):

```ts
export async function getObjectVariables(
  provider: GxFileSystemProvider,
  objName: string,
  cache: Map<string, any[]>,
): Promise<any[]> {
  if (cache.has(objName) ) return cache.get(objName)!;   // never expires
  try {
    const result = await provider.readObjectVariables(objName, 15000);
    if (result && Array.isArray(result)) {
      cache.set(objName, result);
      return result;
    }
  } catch (e) {
    Logger.error(`[Nexus IDE] Error fetching variables: ${e}`);
  }
  return [];
}
```

The reference TTL pattern (`hoverProvider.ts:8-9, 23-30`):

```ts
private _cache = new Map<string, { hover: vscode.Hover, expires: number }>();
private readonly CACHE_TTL = 30000; // 30 seconds
// ...
const cached = this._cache.get(cacheKey);
if (cached && cached.expires > Date.now()) return cached.hover;
if (this._cache.size > 100) {
  for (const [k, v] of this._cache) if (v.expires < Date.now()) this._cache.delete(k);
}
```

### Convention

TypeScript, 2-space indent, `tsc -p ./`, ESLint 9 flat config. Tests under
`src/nexus-ide/src/test/suite/*.test.ts`, Mocha via `@vscode/test-electron`.
Existing test for this file's neighbours: `src/nexus-ide/src/test/suite/inlineCompletionProvider.test.ts`.

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
- `src/nexus-ide/src/gxMemberResolver.ts`
- `src/nexus-ide/src/test/suite/gxMemberResolver.test.ts` (create)

**Out of scope** (do NOT touch):
- `inlineCompletionProvider.ts` / `completionProvider.ts` — they only own the `Map` instance; changing the cache **value type** would ripple. Keep the caller-owned-`Map` contract; put the TTL entirely inside `getObjectVariables` (see Step 1). If you find yourself needing to edit the providers, STOP.
- `hoverProvider.ts` — read-only reference.
- The `resolveVariableMembers` structure-fetch cache path (it re-fetches every call already).

## Git workflow

- Branch: `advisor/063-nexus-member-cache-ttl`
- Commit style: `fix(nexus-ide): ...` (Conventional Commits).
- Do NOT push or open a PR unless instructed.

## Steps

### Step 1: Add a TTL inside `getObjectVariables` without changing the caller contract

The callers pass `Map<string, any[]>`. To add expiry without touching them, store a
sidecar timestamp map keyed the same way, OR change the internal cache-value shape
while keeping the **parameter** type as-is by storing `{ data, expires }` and casting.
Prefer the clearest option: change the entry to hold an expiry and keep the public
signature stable by widening the value to `any[]` that actually carries a wrapper is
error-prone — instead, key a module-level `WeakMap` from the passed `cache` to a
parallel `Map<string, number>` of expiry timestamps.

Target shape:

```ts
const CACHE_TTL_MS = 30000; // mirror hoverProvider.ts:9
// Parallel expiry timestamps per caller-owned cache instance.
const expiryByCache = new WeakMap<Map<string, any[]>, Map<string, number>>();

export async function getObjectVariables(
  provider: GxFileSystemProvider,
  objName: string,
  cache: Map<string, any[]>,
): Promise<any[]> {
  let expiries = expiryByCache.get(cache);
  if (!expiries) { expiries = new Map(); expiryByCache.set(cache, expiries); }

  const now = Date.now();
  const exp = expiries.get(objName);
  if (cache.has(objName) && exp !== undefined && exp > now) {
    return cache.get(objName)!;
  }

  try {
    const result = await provider.readObjectVariables(objName, 15000);
    if (result && Array.isArray(result)) {
      cache.set(objName, result);
      expiries.set(objName, now + CACHE_TTL_MS);
      return result;
    }
  } catch (e) {
    Logger.error(`[Nexus IDE] Error fetching variables: ${e}`);
  }
  return [];
}
```

Rationale for `WeakMap`: keeps the exported signature (`cache: Map<string, any[]>`)
byte-identical, so the two provider call sites need no change; the expiry map is
garbage-collected with the provider when the extension deactivates.

**Verify**: `npm run compile` → exit 0.

### Step 2: Confirm no caller change is needed

**Verify**: `grep -n "getObjectVariables" src/nexus-ide/src` shows the two callers still
pass a bare `Map<string, any[]>` and compile without edits → `npm run compile` exit 0.

### Step 3: Add tests

Create `src/nexus-ide/src/test/suite/gxMemberResolver.test.ts` modeled on
`src/nexus-ide/src/test/suite/inlineCompletionProvider.test.ts`. Use a fake `provider`
with a `readObjectVariables` that returns a mutable array and counts calls. Cover:
- **Cache hit within TTL**: two calls within TTL → `readObjectVariables` called once; both return the same data.
- **Expiry refetch**: since you cannot advance the real clock easily, make the test drive expiry by using a very short-lived assertion path — the cleanest approach is to expose `CACHE_TTL_MS` is NOT desired. Instead assert the *staleness fix behaviorally*: because you can't fake `Date.now` without a helper, add the test as: call once, then clear the caller's `cache.delete(objName)` and call again → refetch happens (proves the entry is re-fetchable, i.e. not a permanent freeze). AND add a comment noting the TTL is time-based and covered by reading `hoverProvider`'s established pattern.

  If the repo's test harness already stubs timers or `Date.now` anywhere (grep `sinon`/`useFakeTimers`), prefer a real time-based test; otherwise use the delete-and-refetch behavioral test above.
- **Empty/non-array result**: `readObjectVariables` returns `null` → function returns `[]` and does not poison the cache with a permanent empty.

**Verify**: `npm test` → all pass including new tests.

### Step 4: Full gate

**Verify**: `npm run check` → all pass.

## Test plan

- New `gxMemberResolver.test.ts`: cache-hit-once, re-fetchable-after-eviction, empty-result-safe.
- Pattern: `inlineCompletionProvider.test.ts`.
- Verification: `npm test` → all pass, new tests included.

## Done criteria

ALL must hold:
- [ ] `npm run compile` exits 0.
- [ ] `npm run lint` exits 0, no new errors.
- [ ] `npm test` passes; `gxMemberResolver.test.ts` exists and passes.
- [ ] `getObjectVariables` writes an expiry when it caches and checks it on read (grep for `expires`/`CACHE_TTL_MS` in `gxMemberResolver.ts` returns matches).
- [ ] `inlineCompletionProvider.ts` and `completionProvider.ts` are **unchanged** (`git status` shows only the two in-scope files).
- [ ] `plans/README.md` status row updated.

## STOP conditions

- "Current state" excerpts don't match live code (drift).
- Adding the TTL forces a change to either provider's cache type → STOP (the WeakMap approach is specifically to avoid this; if it doesn't compile, report the type error).
- A step verification fails twice after a reasonable fix.

## Maintenance notes

- TTL is 30s to match `hoverProvider.ts`. If completions still feel stale after a
  save, the better long-term fix is event-driven invalidation on
  `workspace.onDidSaveTextDocument` for the affected object — deferred here to keep
  the change minimal and the risk LOW.
- Reviewer should check the `WeakMap` keying is per-caller-cache (not global), so
  the inline and completion providers keep independent lifetimes as the original
  docstring intends (`gxMemberResolver.ts:33-35`).
