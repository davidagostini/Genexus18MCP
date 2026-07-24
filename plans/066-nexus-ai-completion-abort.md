# Plan 066: Abort in-flight AI inline-completion requests on cancellation/timeout

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 98b9a7d..HEAD -- src/nexus-ide/src/inlineCompletionProvider.ts src/nexus-ide/src/infra/GxGatewayClient.ts src/nexus-ide/src/gxFileSystem.ts`
> If any changed since this plan was written, compare the "Current state"
> excerpts against live code before proceeding; on a mismatch, STOP.

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: MED
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `98b9a7d`, 2026-07-23

## Why this matters

The opt-in AI inline-completion path races the MCP call against a 4-second timeout
with `Promise.race`, but the underlying HTTP request is **never aborted** — when the
timeout wins (or VS Code cancels the completion), the request keeps running to
completion server-side and its result is silently dropped. Under a slow/degraded
gateway, rapid typing piles up N abandoned in-flight requests, wasting worker capacity
and pinning the "GeneXus MCP: N ops" status-bar busy indicator well after the user has
moved on. Wiring true cancellation (`req.destroy()` on the VS Code
`CancellationToken`) turns a discarded-result leak into a real abort.

## Current state

Files:
- `src/nexus-ide/src/inlineCompletionProvider.ts` — `resolveAiGhostText` does the `Promise.race`.
- `src/nexus-ide/src/infra/GxGatewayClient.ts` — `callMcpTool → callMcp → postRawJsonRpc`; owns the raw `http.request`.
- `src/nexus-ide/src/gxFileSystem.ts` — `GxFileSystemProvider.callMcpTool` is what the provider actually calls (it delegates to the gateway client).

The race with no abort (`inlineCompletionProvider.ts:90-105`):

```ts
const result = await Promise.race([
  this.provider!.callMcpTool("genexus_ai_complete", { context }),
  new Promise<undefined>((resolve) => setTimeout(() => resolve(undefined), AI_TIMEOUT_MS)),
]);
if (token.isCancellationRequested || !result || result.code === "AiEndpointNotConfigured" || !result.completion) {
  return [];
}
```

The request has a `timeout` already (server-side socket timeout), but no path to
`req.destroy()` on caller cancellation (`GxGatewayClient.ts:276-320`). The call chain
signatures:

```ts
// GxGatewayClient.ts
async callMcp(method: string, params?: any, customTimeout?: number): Promise<any>            // :72
async callMcpTool(name: string, args?: any, customTimeout?: number): Promise<any>            // :142
private async postRawJsonRpc(targetUrl, command, customTimeout?, extraHeaders?): Promise<…>  // :249
```

`postRawJsonRpc` already has the destroy primitive wired for the timeout case
(`GxGatewayClient.ts:303-310` calls `req.destroy()`), so the abort mechanism exists —
it just isn't reachable from a caller's `CancellationToken`.

`GxFileSystemProvider.callMcpTool` (in `gxFileSystem.ts`) is the method the inline
provider calls (`this.provider!.callMcpTool(...)`); it forwards to the gateway client.
Grep it to see its exact signature before editing: `grep -n "callMcpTool" src/nexus-ide/src/gxFileSystem.ts`.

### Convention

TypeScript, 2-space indent, `tsc -p ./`, ESLint 9. Tests:
`src/nexus-ide/src/test/suite/gxGatewayClient.test.ts` and
`inlineCompletionProvider.test.ts` exist — extend them.

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
- `src/nexus-ide/src/infra/GxGatewayClient.ts` — thread an optional `AbortSignal` through `callMcpTool → callMcp → postRawJsonRpc`; destroy the request when it fires.
- `src/nexus-ide/src/gxFileSystem.ts` — forward the optional `AbortSignal` param through `GxFileSystemProvider.callMcpTool`.
- `src/nexus-ide/src/inlineCompletionProvider.ts` — create an `AbortController`, wire it to the `CancellationToken` + the timeout, pass its signal.
- `src/nexus-ide/src/test/suite/gxGatewayClient.test.ts` and/or `inlineCompletionProvider.test.ts` (extend).

**Out of scope**:
- Retry / session-recovery logic in `callMcp` (`GxGatewayClient.ts:72+`) — preserve it exactly; an aborted request must NOT trigger the retriable-transport-error retry (an abort is intentional, not a transport failure — handle this in Step 2).
- The `static activeRequests`/status-bar bookkeeping shape — leave the `finished`-flag mechanism; just ensure an aborted request still reaches `finishTrackedRequest` exactly once (via the `error` handler that `req.destroy()` triggers).
- Any other caller of `callMcpTool` — the new param is **optional**, so existing callers compile unchanged.

## Git workflow

- Branch: `advisor/066-nexus-ai-completion-abort`
- Commit style: `fix(nexus-ide): ...`.
- Do NOT push or open a PR unless instructed.

## Steps

### Step 1: Thread an optional `AbortSignal` down to the request

Use the Node `AbortSignal` (Node 20 per `@types/node`), which `http.request` accepts
via its options `signal`. Add an optional trailing `signal?: AbortSignal` param to
`callMcpTool`, `callMcp`, and `postRawJsonRpc` in `GxGatewayClient.ts`, forwarding it
down. In `postRawJsonRpc`, pass `signal` into the `http.request` options object
(alongside `method`/`headers`/`timeout`). Node will emit an `'error'` with
`err.name === 'AbortError'` / code `ABORT_ERR` and destroy the socket when the signal
aborts — the existing `req.on("error", ...)` handler (`GxGatewayClient.ts:312-318`)
will fire and settle the promise + call `finishTrackedRequest` once (guarded by
`finished`).

**Verify**: `npm run compile` → exit 0.

### Step 2: Do not retry an intentional abort

In `callMcp` (`GxGatewayClient.ts:72+`), the retry loop keys off
`isRetriableTransportError`. Ensure an abort is **not** retried: before/inside the
catch that decides to retry, if the error is an abort
(`error?.name === 'AbortError' || (error as any)?.code === 'ABORT_ERR'`), rethrow
immediately (or return a benign empty result) instead of retrying. Read the actual
retry structure first (`grep -n "isRetriableTransportError\|for\|retry\|attempt" src/nexus-ide/src/infra/GxGatewayClient.ts`) and insert the abort short-circuit at the top of the retry decision.

**Verify**: `npm run compile` → exit 0.

### Step 3: Forward the signal through `GxFileSystemProvider.callMcpTool`

Add the optional `signal?: AbortSignal` param to `GxFileSystemProvider.callMcpTool`
(`gxFileSystem.ts`) and forward it to the gateway client's `callMcpTool`. Keep it
optional so all other call sites are unaffected.

**Verify**: `npm run compile` → exit 0.

### Step 4: Wire the inline provider to actually abort

In `resolveAiGhostText` (`inlineCompletionProvider.ts:74-117`), replace the
result-discard race with a real abort:

```ts
const controller = new AbortController();
const cancelSub = token.onCancellationRequested(() => controller.abort());
const timer = setTimeout(() => controller.abort(), AI_TIMEOUT_MS);
try {
  const result = await this.provider!.callMcpTool(
    "genexus_ai_complete", { context }, undefined, controller.signal,
  );
  if (token.isCancellationRequested || !result || result.code === "AiEndpointNotConfigured" || !result.completion) {
    return [];
  }
  return [new vscode.InlineCompletionItem(String(result.completion), new vscode.Range(position, position))];
} catch (e) {
  Logger.debug(`[Nexus IDE] AI inline completion unavailable: ${e}`);
  return [];
} finally {
  clearTimeout(timer);
  cancelSub.dispose();
}
```

Notes:
- The 4th positional arg to `callMcpTool` must line up with the signature you set in
  Step 3 (name, args, customTimeout, signal). If `GxFileSystemProvider.callMcpTool`
  has a different arg order, match it — do not add a 4th positional that lands in the
  wrong slot.
- On abort, `callMcpTool` rejects with an `AbortError` → caught → returns `[]`. That's
  the desired "no ghost text" outcome, and now the HTTP request is actually torn down.

**Verify**: `npm run compile` → exit 0.

### Step 5: Tests

Extend `src/nexus-ide/src/test/suite/gxGatewayClient.test.ts` (or add a focused test):
- **Abort tears down the request**: start a `postRawJsonRpc`/`callMcpTool` against a
  stub HTTP server (the existing test file already stands one up — reuse it) that
  never responds; abort the signal; assert the returned promise rejects with an
  abort-shaped error and the server saw the socket close (or `activeRequests` returns
  to 0). If the existing harness can't easily observe socket close, assert instead
  that the promise rejects promptly on abort (not after the full timeout) and
  `GxGatewayClient.activeRequests` is 0 afterward.
- **Abort is not retried**: with a stub that would satisfy `isRetriableTransportError`
  on a normal error, confirm an aborted call does **not** loop/retry (call count == 1).

If wiring an abort test into the electron harness proves impractical, add at minimum a
unit-level test in `inlineCompletionProvider.test.ts` that stubs `provider.callMcpTool`
to capture the 4th arg and asserts an `AbortSignal` is passed, and that
`token.onCancellationRequested` triggers `controller.abort()` (observable via the
signal's `aborted` flag). Document in the test file why the deeper socket assertion
was or wasn't feasible.

**Verify**: `npm test` → all pass including the new test(s).

### Step 6: Full gate

**Verify**: `npm run check` → all pass.

## Test plan

- `gxGatewayClient.test.ts`: abort-rejects-promptly + not-retried (or the fallback
  signal-passed unit test in `inlineCompletionProvider.test.ts` if the harness can't
  observe socket teardown — state which you used and why).
- Pattern: the existing stub-server tests in `gxGatewayClient.test.ts`.
- Verification: `npm test` → all pass.

## Done criteria

ALL must hold:
- [ ] `npm run compile` exits 0.
- [ ] `npm run lint` exits 0, no new errors.
- [ ] `npm test` passes; new abort test(s) present and passing.
- [ ] `postRawJsonRpc` passes a `signal` into `http.request` options (grep confirms).
- [ ] `callMcpTool`/`callMcp` accept an optional `signal` and an abort is short-circuited out of the retry path.
- [ ] `resolveAiGhostText` uses an `AbortController` wired to both `token.onCancellationRequested` and the timeout, and disposes the subscription + clears the timer in `finally`.
- [ ] Only the in-scope files modified (`git status`).
- [ ] `plans/README.md` status row updated.

## STOP conditions

- "Current state" excerpts don't match live code (drift).
- The Node version's `http.request` does not honor `signal` (it does since Node 15; `@types/node` is ^20) — if a compile error says `signal` isn't a valid option, STOP and report (may need `AbortController` polyfill or an explicit `req.destroy()` on an `abort` listener instead).
- Threading the signal forces a **breaking** change to an existing `callMcpTool` caller (i.e. the param can't be made optional) — STOP; the whole point is a non-breaking optional param.
- A step verification fails twice after a reasonable fix.

## Maintenance notes

- Only the AI ghost-text path is wired to abort here. The member-resolution path
  (`resolveMemberGhostText`) already returns fast and isn't a leak source; leave it.
  If other long-running features later want cancellation, the `signal` param now
  exists on the whole chain to reuse.
- Reviewer should confirm: an aborted request settles the `activeRequests` counter
  exactly once (no double-decrement, no leak) and never triggers the transport-retry.
- Related smell (not fixed here, noted for a future pass): the `finished`-flag
  bookkeeping in `postRawJsonRpc` is duplicated across three listeners; centralizing
  it into one idempotent `settle()` would make this counter harder to break, but is
  out of scope for this plan.
