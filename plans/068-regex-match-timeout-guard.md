# Plan 068: Bounded match-timeout for LLM-supplied regex patterns (search_source + read_logs grep)

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat e756dd2..HEAD -- src/GxMcp.Worker/Services/SourceSearchService.cs src/GxMcp.Worker/Services/ObjectService.cs src/GxMcp.Worker.Tests/SourceSearchPerfGuardTests.cs src/GxMcp.Worker.Tests/LogFilteringTests.cs`
> If any changed since this plan was written, compare the "Current state"
> excerpts against live code before proceeding; on a mismatch, STOP.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: correctness / availability (regex-engine hang)
- **Planned at**: commit `e756dd2`, 2026-08-10

## Why this matters

`genexus_search_source pattern=<regex>` and `genexus_read_logs grepPattern=<regex>`
compile an **LLM-supplied regex with no match timeout**. .NET Framework's default is
`Regex.InfiniteMatchTimeout` (the Worker `App.config` sets no
`REGEX_DEFAULT_MATCH_TIMEOUT`), so a catastrophic-backtracking pattern — e.g.
`(a+)+$` against a long source line, or `(\w+\s?)+` style nested quantifiers — can
run for minutes on a single `IsMatch` call.

The search runs on the **single STA SDK thread** (the main dispatcher routes
non-threadsafe commands to `SdkCommandQueue`; `CommandDispatcher.IsThreadSafe`
returns true only for `control/Cancel` and index-status). So one hung `rx.IsMatch`
blocks **every** subsequent tool call to that KB. The worker's own wall-clock budget
(`TimeoutMs`, default 30s) is only checked **between** entries, never *inside* a
single regex call, and the gateway's 60s tool timeout can't interrupt the worker
thread — recovery is only the 15-minute wedged-kill (`WorkerProcess.cs:176-195`).
The 16-entry compiled-regex cache (`SourceSearchService.GetCachedRegex`) makes a
repeat pathological pattern return instantly to the same hang.

A bounded match timeout converts a KB-wide 15-minute outage into a structured,
per-call `PatternTimeout` error the agent can react to.

## Current state

`src/GxMcp.Worker/Services/SourceSearchService.cs:60-74` — the compiled cache builds
regexes with **no timeout**:

```csharp
private static Regex GetCachedRegex(string pattern, RegexOptions opts)
{
    string key = pattern + "\u0001" + ((int)opts).ToString();
    if (_compiledRegexCache.TryGetValue(key, out var cached)) return cached;
    var fresh = new Regex(pattern, opts);              // <-- no match timeout
    if (_compiledRegexCache.Count < CompiledRegexCacheMaxEntries)
    {
        _compiledRegexCache.TryAdd(key, fresh);
    }
    return fresh;
}
```

All match sites (`SearchCore`) call `rx.IsMatch(ln)` / `rx.Matches(src)` /
`rx.IsMatch(fieldValue)` with no timeout protection.

`src/GxMcp.Worker/Services/ObjectService.cs:528` — `ReadLogs` grep filter, same issue:

```csharp
var rx = new System.Text.RegularExpressions.Regex(grepPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
filtered = filtered.Where(l => rx.IsMatch(l));
```

The surrounding `try/catch` only handles an invalid pattern (falls back to
substring) — a **valid but pathological** pattern is not caught because it never
throws; it hangs.

### Convention

C# (.NET Framework 4.8, x86), match the surrounding file style. Tests: xUnit,
reflection into `internal`/`private` static members is the established pattern
(see `SourceSearchPerfGuardTests` and `LogFilteringTests`).

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build worker | `$env:GX_PATH = 'C:\Program Files (x86)\GeneXus\GeneXus18'; dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` | exit 0 |
| Run one test file | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~SourceSearchPerfGuardTests"` | all pass |
| Run the other | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~LogFilteringTests"` | all pass |
| Full worker tests | `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` | ~1,790 pass (known flaky pair: `EdgeCaseRegressionTests.Dispatcher_PatchApply_ValidateOnly_MapsToDryRun_ViaConvention`, occasionally `PatternApplyServiceTests.*` — treat single failures as flakes and re-run isolated) |

## Scope

**In scope**:
- `src/GxMcp.Worker/Services/SourceSearchService.cs` — bounded timeout on the regex used by `genexus_search_source`; structured `PatternTimeout` envelope on `RegexMatchTimeoutException`.
- `src/GxMcp.Worker/Services/ObjectService.cs` — bounded timeout on the `ReadLogs` grep filter; graceful fallback on timeout.
- `src/GxMcp.Worker.Tests/SourceSearchPerfGuardTests.cs` (extend).
- `src/GxMcp.Worker.Tests/LogFilteringTests.cs` (extend).

**Out of scope**:
- Regexes in `SecurityScanService` / `SqlInjectionScanner` / `VoiceIntentService` — those patterns are hardcoded (not LLM-supplied), so a timeout is unnecessary there.
- The `TimeoutMs` / cursor machinery of `SourceSearchService` — untouched.
- `App.config` `REGEX_DEFAULT_MATCH_TIMEOUT` — a per-call `TimeSpan` is more targeted and testable; do NOT add the AppContext switch (it would also time out the hardcoded regexes above).
- Any change to the `GetCachedRegex` cache-key semantics beyond carrying the timeout.

## Git workflow

- Branch: `advisor/068-regex-match-timeout-guard`
- Commit style: `fix(worker): bound regex match time on LLM-supplied patterns`
- Do NOT push or open a PR unless instructed.

## Steps

### Step 1: Add a bounded timeout constant to `SourceSearchService`

Add near the compiled-regex cache (around `SourceSearchService.cs:55`):

```csharp
// AVAILABILITY: LLM-supplied patterns must not hang the single STA thread.
// .NET Framework defaults to Regex.InfiniteMatchTimeout, so a catastrophic
// back-tracking pattern (e.g. "(a+)+$") blocks every later KB call until the
// 15-min wedged kill. Bound each match call; the caller maps the resulting
// RegexMatchTimeoutException to a structured PatternTimeout envelope.
internal static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(2);
```

Keep the key the same (pattern + opts) — the timeout is a constant, so the
cache semantics are unchanged.

**Verify**: `dotnet build src\GxMcp.Worker\GxMcp.Worker.csproj -v:minimal` → exit 0.

### Step 2: Pass the timeout in `GetCachedRegex`

Change `var fresh = new Regex(pattern, opts);` to:

```csharp
var fresh = new Regex(pattern, opts, RegexMatchTimeout);
```

**Verify**: build → exit 0.

### Step 3: Catch `RegexMatchTimeoutException` in `SearchCore` and emit `PatternTimeout`

In `SearchCore` (`SourceSearchService.cs:307` region), the single `try` wraps the
whole scan. Add a dedicated `catch` clause before the generic `catch (Exception ex)`:

```csharp
catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
{
    return Models.McpResponse.Err(
        code: "PatternTimeout",
        message: "The regex pattern exceeded the " + RegexMatchTimeout.TotalSeconds
            + "s match-timeout on one input and was aborted.",
        hint: "Simplify the pattern — avoid nested quantifiers like (a+)+ or (\\w+\\s?)+ "
            + "that can backtrack exponentially. Prefer literal tokens or atomic groups.");
}
```

Place it immediately before the existing `catch (Exception ex)` that returns
`SourceSearchFailed`, so the more specific exception wins.

**Verify**: build → exit 0.

### Step 4: Bound the `ReadLogs` grep filter

In `ObjectService.ReadLogs` (`ObjectService.cs:526-532`), the grep block currently
is:

```csharp
if (!string.IsNullOrWhiteSpace(grepPattern))
{
    try
    {
        var rx = new System.Text.RegularExpressions.Regex(grepPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        filtered = filtered.Where(l => rx.IsMatch(l));
    }
    catch { /* invalid regex falls back to substring */ filtered = filtered.Where(l => l.IndexOf(grepPattern, StringComparison.OrdinalIgnoreCase) >= 0); }
}
```

Change it to:

```csharp
if (!string.IsNullOrWhiteSpace(grepPattern))
{
    try
    {
        var rx = new System.Text.RegularExpressions.Regex(grepPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            GxMcp.Worker.Services.SourceSearchService.RegexMatchTimeout);
        filtered = filtered.Where(l => rx.IsMatch(l));
    }
    catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
    {
        // A valid-but-pathological pattern must not hang the STA thread; degrade
        // to the same substring fallback an invalid pattern gets.
        filtered = filtered.Where(l => l.IndexOf(grepPattern, StringComparison.OrdinalIgnoreCase) >= 0);
    }
    catch { /* invalid regex falls back to substring */ filtered = filtered.Where(l => l.IndexOf(grepPattern, StringComparison.OrdinalIgnoreCase) >= 0); }
}
```

Reuse the `SourceSearchService.RegexMatchTimeout` constant (it's `internal`, same
assembly) rather than duplicating the value. Do NOT add a public surface change to
`ReadLogs`.

**Verify**: build → exit 0.

### Step 5: Tests

**`SourceSearchPerfGuardTests.cs`** — add:

1. **Timeout is applied** — reflectively call `GetCachedRegex("a", RegexOptions.Compiled)`,
   read the returned `Regex.MatchTimeout` (the `Regex` type exposes `MatchTimeout`),
   and assert `> TimeSpan.Zero` and `<= TimeSpan.FromSeconds(3)`.
2. **Pathological pattern aborts with a structured envelope** — build a
   `SourceSearchService` with a stub `ObjectService` (follow the null-seam pattern:
   `SearchAsJson` works when `_objectService` is null for index-only paths — see how
   `SourceSearchEnvelopeTests`/`SourceSearchCancellationTests` construct it) and an
   in-memory index containing one entry whose cached source is a long run of `a`
   (e.g. 20_000 chars). Call `SearchAsJson` with `pattern="(a+)+$"` and assert the
   returned envelope's `error.code` is `PatternTimeout` (the timeout is 2s, so the
   test adds at most 2s). If the null-seam cannot reach the regex path for a cached
   entry, use the smallest real fixture that does; document what you used.
   **Important**: the `SearchAsJson` overload that takes a `CancellationToken` is the
   one `SearchCore` uses; call that.

**`LogFilteringTests.cs`** — add:

3. **Pathological grepPattern degrades to substring** — write a log file whose
   content makes a catastrophic pattern time out (e.g. a single line of ~50k `a`
   chars); call `CallReadLogs(grepPattern: "(a+)+$")`; assert the call **returns**
   (does not hang beyond the 2s bound) and the `status` is `ok` (substring fallback:
   the line contains `a`, so it matches).

**Verify**: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "FullyQualifiedName~SourceSearchPerfGuardTests|FullyQualifiedName~LogFilteringTests"` → all pass.

### Step 6: Full worker test suite

**Verify**: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj` → all pass (treat the two known flaky tests as flakes if they fail in isolation-green fashion).

## Test plan

- `SourceSearchPerfGuardTests.cs`: regex carries a bounded `MatchTimeout`; pathological pattern returns `PatternTimeout` envelope.
- `LogFilteringTests.cs`: pathological `grepPattern` returns quickly with substring-fallback behavior.
- Pattern: existing `SourceSearchPerfGuardTests` reflection helpers + `LogFilteringTests` `logPathOverride` seam.
- Verification: `dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --filter "..."` → all pass.

## Done criteria

ALL must hold:
- [ ] `GetCachedRegex` builds with `RegexMatchTimeout` (grep shows the 3-arg ctor).
- [ ] `SearchCore` has a `catch (RegexMatchTimeoutException)` returning `PatternTimeout` before the generic catch.
- [ ] `ReadLogs` grep uses the 3-arg ctor and a `RegexMatchTimeoutException` fallback to substring.
- [ ] New tests present and passing (timeout-applied; `PatternTimeout` envelope; logs grep degradation).
- [ ] Worker test suite green.
- [ ] Only the in-scope files modified (`git status`).
- [ ] `plans/README.md` status row updated.

## STOP conditions

- "Current state" excerpts don't match live code (drift).
- A legitimate user pattern genuinely needs >2s per match (i.e. a test with a
  *reasonable* pattern hits the timeout) — STOP and report; the fix should not
  break normal searches, so the constant may need raising, not the timeout removing.
- `.NET Framework 4.8`'s `Regex` rejects the 3-arg ctor at compile time (it doesn't —
  the `(string, RegexOptions, TimeSpan)` ctor exists since .NET 4.5) — if it somehow
  doesn't resolve, STOP and report rather than falling back to an infinite timeout.
- A step verification fails twice after a reasonable fix.

## Maintenance notes

- The 2s bound is per `IsMatch`/`Matches` call, not per whole search — the existing
  wall-clock `TimeoutMs` budget still bounds total runtime between entries. A
  genuinely slow-but-correct pattern (rare on single source lines) will get
  `PatternTimeout`; the hint tells the agent how to simplify.
- If a future feature adds another LLM-supplied-regex sink, reuse
  `SourceSearchService.RegexMatchTimeout` + the 3-arg ctor + a timeout catch —
  the pattern in this plan is the template.
- The compiled-regex cache (16 entries) now stores timeout-carrying instances; the
  key is unchanged because the timeout is a constant. If the constant ever becomes
  configurable, the key must include it.
- Reviewer should confirm the `ReadLogs` change reuses the constant rather than a
  magic number, and that the two `catch` clauses can't be collapsed incorrectly
  (`RegexMatchTimeoutException` is a `TimeoutException`, NOT an `ArgumentException`,
  so the existing catch genuinely misses it today).
