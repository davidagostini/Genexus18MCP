using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        // Proactively kick off the KB search index on first MCP initialize so the
        // first `genexus_query` doesn't pay the full cold-start cost. Worker side
        // short-circuits to "AlreadyIndexed" if cache is warm, so this is cheap on
        // warm starts. When a real cold-start kicks in, an upfront
        // notifications/message tells the agent that search/analyze return partial
        // results while indexing runs in the background — read/edit/build are
        // immediate regardless.
        private static void TriggerIndexBootstrapOnce(string? kbAlias = null)
        {
            string bootstrapKey = NormalizeKbAlias(kbAlias) ?? NormalizeKbAlias(_currentKb.Value?.NormalizedAlias)
                ?? "__default__";
            if (!_indexBootstrapStartedByKb.TryAdd(bootstrapKey, 0)) return;

            if (IndexBootstrapTriggerForTest != null)
            {
                IndexBootstrapTriggerForTest();
                return;
            }

            Log("[IndexBootstrap] firing on initialize");

            _ = Task.Run(async () =>
            {
                KbHandle? previousKb = _currentKb.Value;
                bool previousOwnerRequirement = _currentOperationRequiresOwner.Value;
                try
                {
                    if (_workerPool == null) { Log("[IndexBootstrap] worker pool null"); return; }

                    if (bootstrapKey != "__default__")
                    {
                        _currentKb.Value = _workerPool.ListOpen()
                            .FirstOrDefault(h => string.Equals(h.NormalizedAlias, bootstrapKey, StringComparison.OrdinalIgnoreCase));
                        if (_currentKb.Value == null)
                        {
                            Log($"[IndexBootstrap] KB '{bootstrapKey}' is no longer open");
                            return;
                        }
                    }

                    // Warmup and index bootstrap both acquire the default KB. Serialize
                    // them so initialize cannot create two Workers for the same KB and
                    // leave the gateway holding the BusyRejecting process.
                    await WorkerWarmupCompleted.Task.ConfigureAwait(false);

                    var indexCommand = new JObject
                    {
                        ["module"] = "KB",
                        ["action"] = "BulkIndex",
                        ["client"] = "mcp"
                    };

                    // Bootstrap is an internal gateway operation, not a client mutation;
                    // it must not require the caller's session lease after an explicit open
                    // or reload selected the worker by alias.
                    _currentOperationRequiresOwner.Value = false;
                    var resp = await SendWorkerCommandAsync(
                        indexCommand,
                        30000,
                        "Index bootstrap timeout",
                        wr => wr,
                        (_, correlationId) => new JObject(),
                        toolName: "gateway_index_bootstrap",
                        trackOperation: false);

                    // BulkIndex now returns the canonical envelope ({status:"ok", code, result}).
                    // The fresh-vs-warm signal lives in `code`; fall back to the legacy top-level
                    // `status` for any pre-canonical worker still in the pool.
                    var result = resp?["result"] as JObject;
                    string? status = result?["code"]?.ToString()
                        ?? result?["status"]?.ToString();
                    Log($"[IndexBootstrap] worker reply code={status ?? "<null>"}");

                    // The default lite-index path returns "LiteStarted"; the legacy full path
                    // returns "Started". Either means a fresh cold-start index just kicked off,
                    // so the agent should see the one-time background-indexing notice.
                    // ("AlreadyIndexed" / "AlreadyInProgress" / "DeltaStarted" are warm starts — no notice.)
                    if (string.Equals(status, "Started", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(status, "LiteStarted", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("[IndexBootstrap] emitting cold-start notice");
                        BroadcastNotification("notifications/message", new
                        {
                            level = "info",
                            logger = "indexing",
                            data = "First-time indexing of this KB has started in the background. "
                                + "Search and analyze tools will return partial results while it runs; "
                                + "read, edit, build, and list tools are immediate and unaffected. "
                                + "Watch notifications/progress for live progress."
                        });
                    }

                    // The first-touch warm pass can only resolve its probe object once the
                    // index is listable. On a cold start the warmup's single list attempt runs
                    // before this bootstrap, sees the IndexNotReady envelope (no items) and
                    // would skip the pass — leaving the first-touch JIT/SDK cost on the agent's
                    // read/inspect/edit calls. Now that the index has been kicked (or was
                    // already warm), wait for a listable object here and warm.
                    await RunFirstTouchWarmOnceAsync(
                        ResolveWarmupProbeObjectAsync,
                        () => Log("[Warmup] Probe object not listable yet (index still building); waiting before the first-touch warm pass."));
                }
                catch (Exception ex)
                {
                    Log($"[IndexBootstrap] {ex.Message}");
                }
                finally
                {
                    _currentKb.Value = previousKb;
                    _currentOperationRequiresOwner.Value = previousOwnerRequirement;
                }
            });
        }

        private static void TriggerWorkerWarmupOnce()
        {
            if (Interlocked.CompareExchange(ref _workerWarmupStarted, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_workerPool == null)
                    {
                        Log("[Warmup] WorkerPool not available, skipping warmup.");
                        return;
                    }

                    // Resolve the configured default KB before issuing the warmup
                    // command. Strict resolution does not auto-open declared KBs,
                    // so the command would otherwise fail when initialize starts
                    // with no worker already attached.
                    await PrespawnDefaultKbWorkerAsync();

                    Log("[Warmup] Starting worker warmup sequence...");
                    BroadcastNotification("notifications/message", new
                    {
                        level = "info",
                        logger = "warmup",
                        data = "Worker warmup started.",
                        timestamp = DateTime.UtcNow
                    });

                    string? objectName = await ResolveWarmupProbeObjectAsync();
                    if (!string.IsNullOrWhiteSpace(objectName))
                    {
                        // Warm start: the index is already listable, so the fast path runs the
                        // pass right here instead of waiting for the index-bootstrap trigger.
                        await RunFirstTouchWarmOnceAsync(() => Task.FromResult<string?>(objectName), onWaitingForProbe: null);
                    }

                    Log("[Warmup] Worker warmup finished.");
                    BroadcastNotification("notifications/message", new
                    {
                        level = "info",
                        logger = "warmup",
                        data = "Worker warmup finished.",
                        timestamp = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    Log("[Warmup] Worker warmup failed: " + ex.Message);
                    BroadcastNotification("notifications/message", new
                    {
                        level = "warning",
                        logger = "warmup",
                        data = "Worker warmup failed: " + ex.Message,
                        timestamp = DateTime.UtcNow
                    });
                }
                finally
                {
                    WorkerWarmupCompleted.TrySetResult(true);
                }
            });
        }

        // The first-touch warm pass needs a listable probe object. On a cold start the
        // warmup's single List/Objects attempt runs *before* the index is listable, so it
        // gets the IndexNotReady envelope (no items) — the previous single-shot resolve
        // dropped the entire pass there, and the agent's first read/inspect/edit paid the
        // one-time JIT/SDK first-touch cost instead (measured on a real KB: first read
        // 174ms, inspect 78ms, edit dry-run 29ms, against ~1ms for every following call).
        // The index-bootstrap path now waits — bounded — for a listable object and then
        // warms; the budget exists only so a permanently unavailable index cannot leave a
        // background loop alive. Do not move this wait into the pre-bootstrap warmup: the
        // bootstrap itself awaits WorkerWarmupCompleted.
        private const int WarmupProbeMaxAttempts = 40;
        private const int WarmupProbeResolveRetryDelayMs = 3000;

        // Test seam (same pattern as RespawnDelayForTest): the production retry interval
        // would make the bounded-wait regression test run the full 40 × 3s budget.
        internal static int? WarmupProbeRetryDelayMsForTest;
        internal static int WarmupProbeAttemptsForTest => WarmupProbeMaxAttempts;

        private static async Task<string?> ResolveWarmupProbeObjectAsync()
        {
            var listCommand = new JObject
            {
                ["module"] = "List",
                ["action"] = "Objects",
                ["target"] = string.Empty,
                // Prefer a real code object for the first-touch warm: Folders/Modules
                // (alphabetically first in the index) exercise almost no SDK path.
                // A Transaction or Procedure touches structure/source readers — the
                // paths inspect/analyze/read actually hit on the agent's first call.
                ["typeFilter"] = "Transaction,Procedure",
                ["limit"] = 1,
                ["offset"] = 0,
                ["client"] = "mcp"
            };

            var listResponse = await SendWorkerCommandAsync(
                listCommand,
                30000,
                "Warmup list timeout",
                workerResponse => workerResponse,
                (_, correlationId) => new JObject
                {
                    ["error"] = new JObject
                    {
                        ["message"] = "Warmup list operation timed out.",
                        ["correlationId"] = correlationId
                    }
                },
                toolName: "gateway_warmup_list",
                trackOperation: false);

            return ExtractWarmupProbeObjectName(listResponse?["result"]);
        }

        // Any reply shape that carries no listable object — the IndexNotReady envelope
        // while indexing, an error envelope, empty results — yields null so the caller
        // retries instead of warming nothing.
        internal static string? ExtractWarmupProbeObjectName(JToken? result)
        {
            JArray? items = null;
            if (result is JObject obj)
            {
                items = (obj["results"] ?? obj["objects"]) as JArray;
            }
            else if (result is JArray arr)
            {
                items = arr;
            }

            string? name = items?.FirstOrDefault()?["name"]?.ToString();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        // Bounded retry around the probe resolve. Extracted so the retry/fail-open
        // behavior is unit-testable without a live worker.
        internal static async Task<string?> AwaitWarmupProbeObjectAsync(
            Func<Task<string?>> resolve,
            int maxAttempts = WarmupProbeMaxAttempts,
            int retryDelayMs = WarmupProbeResolveRetryDelayMs,
            Action<int>? onRetry = null)
        {
            if (resolve == null) return null;
            int attempts = Math.Max(1, maxAttempts);
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                string? name = null;
                try
                {
                    name = await resolve().ConfigureAwait(false);
                }
                catch
                {
                    // A failed probe resolve is a warmup miss, never a request failure.
                }

                if (!string.IsNullOrWhiteSpace(name)) return name;
                if (attempt == attempts) break;

                try { onRetry?.Invoke(attempt); } catch { }
                if (retryDelayMs > 0) await Task.Delay(retryDelayMs).ConfigureAwait(false);
            }

            return null;
        }

        // Runs the first-touch warm pass exactly once per gateway. Two callers race for it:
        // the fast path (index already listable right after pre-spawn) and the index-bootstrap
        // path (cold start, where List/Objects answers the IndexNotReady envelope until
        // BulkIndex finishes). Whoever claims the flag warms; the other returns immediately.
        private static int _firstTouchWarmClaimed;

        internal static async Task RunFirstTouchWarmOnceAsync(Func<Task<string?>> resolveProbe, Action? onWaitingForProbe, Func<string, Task>? warmPass = null)
        {
            if (Interlocked.CompareExchange(ref _firstTouchWarmClaimed, 1, 0) != 0) return;

            try
            {
                string? objectName = await AwaitWarmupProbeObjectAsync(
                    resolveProbe,
                    WarmupProbeMaxAttempts,
                    WarmupProbeRetryDelayMsForTest ?? WarmupProbeResolveRetryDelayMs,
                    attempt =>
                    {
                        if (attempt == 1) onWaitingForProbe?.Invoke();
                    });

                if (string.IsNullOrWhiteSpace(objectName))
                {
                    Log("[Warmup] No listable probe object within the wait budget; first-touch warm pass skipped.");
                    return;
                }

                await (warmPass ?? WarmFirstTouchPathsAsync)(objectName);
                Log("[Warmup] First-touch warm pass finished.");
            }
            catch (Exception ex)
            {
                Log("[Warmup] First-touch warm pass failed: " + ex.Message);
            }
        }

        internal static void ResetFirstTouchWarmForTest()
        {
            Interlocked.Exchange(ref _firstTouchWarmClaimed, 0);
        }

        // Pre-spawn the configured default KB's worker via the same AcquireAsync path the
        // explicit `genexus_kb action=open` uses. Fire-and-forget from initialize; errors
        // are swallowed (the regular resolve path re-tries on demand).
        private static async Task PrespawnDefaultKbWorkerAsync()
        {
            try
            {
                string? defaultAlias = GetConfiguredDefaultKb();
                var entry = (_activeConfig?.Environment?.KBs ?? new List<KbEntry>())
                    .FirstOrDefault(k => string.Equals(k.Alias, defaultAlias, StringComparison.OrdinalIgnoreCase));
                if (entry == null || _workerPool == null)
                {
                    Log("[Warmup] No default KB declared — skipping pre-spawn.");
                    return;
                }

                var handle = new KbHandle(entry.Alias, entry.Path);
                Log($"[Warmup] Pre-spawning worker for default KB '{entry.Alias}' ({entry.Path})");
                await _workerPool.AcquireAsync(handle, CancellationToken.None);
                Log($"[Warmup] Pre-spawn of '{entry.Alias}' completed.");
            }
            catch (Exception ex)
            {
                // Non-fatal: the first real call falls back to the standard open path.
                Log("[Warmup] Default-KB pre-spawn skipped: " + ex.Message);
            }
        }

        // First-touch penalty warmer. Measured ([TOOL-LATENCY], scratch gateway vs real KB):
        // the FIRST call of each STA-heavy tool after a worker cold start pays a one-time
        // JIT/SDK-deserialization cost (inspect: up to 3.8s; analyze linter/callers: 0.6s+)
        // while every subsequent call returns in single-digit ms. Exercising those paths
        // here — in the background, right after pre-spawn/index bootstrap — moves that cost
        // out of the agent's turn entirely. Every sub-call is best-effort and individually
        // guarded: a warm failure must never break the warmup sequence.
        internal static List<(string toolName, JObject command)> BuildWarmupCommands(string probeObjectName)
        {
            var commands = new List<(string toolName, JObject command)>();
            foreach (var (canonicalToolName, toolArgs) in new[]
            {
                ("genexus_read",    new JObject { ["name"] = probeObjectName, ["part"] = "Structure" }),
                // Agents read Source, not Structure: the Source reader (ReadSource/ISource)
                // is a different SDK path than the Structure part walker, so warming only
                // Structure left the first Source read at ~150ms (measured).
                ("genexus_read",    new JObject { ["name"] = probeObjectName, ["part"] = "Source" }),
                ("genexus_inspect", new JObject { ["name"] = probeObjectName }),
                ("genexus_analyze", new JObject { ["mode"] = "linter", ["target"] = probeObjectName }),
                ("genexus_analyze", new JObject { ["mode"] = "callers", ["target"] = probeObjectName }),
            })
            {
                var converted = McpRouter.ConvertToolCall(new JObject
                {
                    ["method"] = "tools/call",
                    ["params"] = new JObject
                    {
                        ["name"] = canonicalToolName,
                        ["arguments"] = toolArgs
                    }
                });
                if (converted == null) continue;

                var workerCommand = JObject.FromObject(converted);
                workerCommand["client"] = "mcp";
                commands.Add((canonicalToolName, workerCommand));
            }
            return commands;
        }

        private static async Task WarmFirstTouchPathsAsync(string probeObjectName)
        {
            foreach (var (toolName, command) in BuildWarmupCommands(probeObjectName))
            {
                try
                {
                    await SendWorkerCommandAsync(
                        command,
                        30000,
                        $"Warmup {toolName} timeout",
                        wr => wr,
                        (_, correlationId) => new JObject(),
                        toolName: $"gateway_warmup_{toolName}",
                        trackOperation: false);
                }
                catch (Exception ex)
                {
                    Log($"[Warmup] {toolName} warm step skipped: {ex.Message}");
                }
            }
        }
    }
}
