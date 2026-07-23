# Plan 054: Structured, level-gated logging for Nexus IDE (Phase 2)

> **Executor instructions**: Follow this plan step by step. Run every verification
> command and confirm the expected result before moving on. If a STOP condition occurs,
> stop and report. Update the status row in `plans/README.md` when done. Keep intermediate
> narration minimal and do NOT paste large file/command output into messages (summarize
> counts/exit codes) — produce the final report in one message.
>
> **Drift check (run first)**: `git diff --stat f98ecd0..HEAD -- src/nexus-ide/src`

## Status

- **Priority**: P2
- **Effort**: M-L (mechanical but wide — ~110 call sites)
- **Risk**: LOW (logging swap; no control-flow change)
- **Depends on**: 051 (test baseline)
- **Category**: dx / robustness
- **Planned at**: commit `f98ecd0`, 2026-07-23
- **Roadmap**: `docs/nexus-ide-roadmap.md` Phase 2

## Why this matters

The extension logs via **~110 raw `console.log`/`error`/`warn` calls across 17 files**,
with no level gating — noisy in production, and its only diagnostic trail. There is also
**OutputChannel sprawl**: ad-hoc channels created independently in `extension.ts`
("GeneXus MCP Bootstrap"), `infra/GxGatewayClient.ts` ("GeneXus MCP"), `CommandManager`
("GeneXus Build"/"GeneXus SQL"/"GeneXus Test"), `referenceProvider.ts` ("Nexus IDE
References"). A single level-gated `Logger` (one channel, `genexus.logLevel` setting)
replaces the console noise and gives users/support a real, filterable log — the MCP
server has structured logging; the extension should too.

## Current state

- `grep -rn "console\.\(log\|error\|warn\)" src/nexus-ide/src --include=*.ts | grep -v /test/`
  → ~110 sites across: `completionProvider`, `definitionProvider`, `diagnosticProvider`,
  `extension`, `formatProvider`, `gxFileSystem`, `gxShadowService`, `hoverProvider`,
  `infra/GxGatewayClient`, `managers/{BackendManager,McpDiscoveryManager,ShadowManager,SyncManager}`,
  `referenceProvider`, `signatureHelpProvider`, `webviews/StructureView`, `workspaceSymbolProvider`.
- Existing channels (to consolidate or route through the new Logger):
  `extension.ts:44` `getBootstrapOutput()`, `infra/GxGatewayClient.ts:13` static `outputChannel`,
  `CommandManager.ts:224/316/819`, `referenceProvider.ts:16` static channel.
- `package.json` `contributes.configuration.properties` already has `genexus.*` settings —
  add `genexus.logLevel` there (`error|warn|info|debug`, default `info`).
- Test infra: `src/nexus-ide/src/test/suite/*.test.ts`, `npm run check` gate (compile+lint+test),
  `@vscode/test-electron` runs here (baseline 56 tests).

## Commands you will need

Run from `src/nexus-ide` (own `package.json` + `node_modules`; `npm install` if needed):

| Purpose | Command | Expected |
|---------|---------|----------|
| Compile | `npm run compile` | exit 0 |
| Lint | `npm run lint` | exit 0 (0 errors; ~63 pre-existing warnings baseline) |
| Gate | `npm run check` | compile+lint+test all green |

## Scope

**In scope**:
- A new `src/nexus-ide/src/utils/Logger.ts` (level-gated, single OutputChannel).
- `src/nexus-ide/package.json` (`genexus.logLevel` setting).
- Every non-test `.ts` with `console.*` — swap to `Logger`.
- The ad-hoc OutputChannel creators — route through / consolidate under the Logger where it
  doesn't change a deliberately user-facing channel (the "GeneXus Build"/"SQL"/"Test"
  channels are user-facing command output — LEAVE those as their own channels; only replace
  DEBUG-style `console.*`, not intentional user-facing streamed output).
- `src/nexus-ide/src/test/**` (a Logger unit test).

**Out of scope**:
- Control-flow / error-handling changes (that's plan 055 — here, a `console.error` inside a
  catch just becomes `Logger.error`, the catch logic is untouched).
- The build/SQL/test command OutputChannels' CONTENT (they stream tool output to the user — keep them).

## Steps

### Step 1: Logger utility + setting

Create `Logger.ts`: a singleton over ONE `vscode.window.createOutputChannel("GeneXus Nexus IDE")`
with `error/warn/info/debug` methods that no-op below the configured level. Read level from
`genexus.logLevel` (default `info`), refreshing on config change. Timestamp + level prefix
each line. Add the `genexus.logLevel` enum setting to `package.json`.

**Verify**: `npm run compile` exit 0. Add a `logger.test.ts` asserting level gating (debug
suppressed at info level; error always emitted) — inject a fake sink so the test doesn't need
a real channel.

### Step 2: Migrate console.* → Logger

Replace `console.log`→`Logger.info` (or `.debug` for chatty/loop logs), `console.warn`→`Logger.warn`,
`console.error`→`Logger.error`, file by file. Do NOT change surrounding logic. For the existing
ad-hoc DEBUG channels (`GxGatewayClient`'s static channel, `referenceProvider`'s, bootstrap),
route through the Logger unless the channel is genuinely user-facing (keep Build/SQL/Test).

**Verify**: `grep -rn "console\.\(log\|error\|warn\)" src/nexus-ide/src --include=*.ts | grep -v /test/`
→ 0 (or a short, explicitly-justified allowlist, e.g. a pre-activation log before the Logger exists — note each). `npm run compile` exit 0.

### Step 3: Gate

**Verify**: `npm run check` → compile 0, lint 0 errors, all tests pass (56 + new logger test). Report counts.

## Done criteria

- [ ] `Logger.ts` exists, level-gated, single channel; `genexus.logLevel` setting added.
- [ ] `console.*` count in non-test src is 0 (or a justified, listed allowlist).
- [ ] `npm run check` green (state test count).
- [ ] Build/SQL/Test user-facing channels unchanged; no control-flow change (diff is log-swaps + Logger + setting).
- [ ] No files outside scope modified; `plans/README.md` status row updated.

## STOP conditions

- Drift: `src/nexus-ide/src` changed since `f98ecd0` in a way that conflicts.
- A `console.*` site is load-bearing (e.g. output parsed by something) — leave it, note it.
- Migrating a channel would remove user-facing output someone relies on — keep that channel, only swap true debug logs.

## Maintenance notes

- Plan 055 (bare-catch audit) will route surfaced errors through this Logger — land 054 first.
- Reviewer: confirm zero control-flow changes (pure log swaps) and that user-facing command
  output channels (Build/SQL/Test) still stream as before.
