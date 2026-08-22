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
        private static void TriggerIndexBootstrapOnce()
        {
            if (Interlocked.CompareExchange(ref _indexBootstrapStarted, 1, 0) != 0) return;

            Log("[IndexBootstrap] firing on initialize");

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_workerPool == null) { Log("[IndexBootstrap] worker pool null"); return; }

                    var indexCommand = new JObject
                    {
                        ["module"] = "KB",
                        ["action"] = "BulkIndex",
                        ["client"] = "mcp"
                    };

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
                }
                catch (Exception ex)
                {
                    Log($"[IndexBootstrap] {ex.Message}");
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

                    // Perf: pre-spawn the default KB's worker BEFORE any agent call so the
                    // ~12s cold-start (SM warmup + SDK init + KB open — measured breakdown in
                    // [COLD-START-BREAKDOWN]) is paid during gateway boot instead of on the
                    // first KB-bound tool call. Best-effort: a failed spawn just logs; the
                    // normal open path still works.
                    await PrespawnDefaultKbWorkerAsync();

                    Log("[Warmup] Starting worker warmup sequence...");
                    BroadcastNotification("notifications/message", new
                    {
                        level = "info",
                        logger = "warmup",
                        data = "Worker warmup started.",
                        timestamp = DateTime.UtcNow
                    });

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

                    var result = listResponse?["result"];
                    JArray? items = null;
                    if (result is JObject obj)
                    {
                        items = (obj["results"] ?? obj["objects"]) as JArray;
                    }
                    else if (result is JArray arr)
                    {
                        items = arr;
                    }

                    string? objectName = items?.FirstOrDefault()?["name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(objectName))
                    {
                        await WarmFirstTouchPathsAsync(objectName);
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
            });
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
        private static async Task WarmFirstTouchPathsAsync(string probeObjectName)
        {
            foreach (var (toolName, args) in new[]
            {
                ("read",     new JObject { ["targets"] = new JArray(probeObjectName), ["part"] = "Structure" }),
                ("inspect", new JObject { ["name"] = probeObjectName }),
                ("analyze", new JObject { ["mode"] = "linter", ["target"] = probeObjectName }),
                ("analyze", new JObject { ["mode"] = "callers", ["target"] = probeObjectName }),
            })
            {
                try
                {
                    await SendWorkerCommandAsync(
                        new JObject(args.Properties().Select(p => (JProperty)p).Cast<object>())
                        {
                            ["module"] = toolName == "read" ? "Read" : toolName,
                            ["action"] = toolName switch
                            {
                                "read" => "ExtractSource",
                                "inspect" => "Inspect",
                                _ => "Analyze"
                            },
                            ["client"] = "mcp"
                        },
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
