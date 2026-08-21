using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;

namespace GxMcp.Gateway
{
    partial class Program
    {
        private const string McpAxiSchemaVersion = "mcp-axi/2";
        private static WorkerPool? _workerPool;
        private static KbResolver? _kbResolver;
        // Set per-call at the top of ProcessMcpRequest; SendWorkerCommandAsync reads it
        // to route the command to the correct WorkerProcess in the pool.
        private static readonly AsyncLocal<KbHandle?> _currentKb = new AsyncLocal<KbHandle?>();
        // Legacy single-worker accessor: returns the worker for the AsyncLocal KB if set,
        // otherwise the worker for the DefaultKb (acquiring it lazily).
        private static async Task<WorkerProcess> GetActiveWorkerAsync()
        {
            if (_workerPool == null) throw new InvalidOperationException("WorkerPool not initialised.");
            KbHandle? kb = _currentKb.Value;
            if (kb == null)
            {
                // Fall back to default for callers outside a tool-call context (warmup, etc.).
                kb = _kbResolver!.Resolve(null, _workerPool.ListOpen(), _workerPool.ListKnown());
            }
            return await _workerPool.AcquireAsync(kb, CancellationToken.None);
        }
        internal static WorkerPool? GetWorkerPool() => _workerPool;
        internal static KbResolver? GetKbResolver() => _kbResolver;
        // Plan 038: minimal accessor so McpRouter (a separate class) can resolve the
        // per-request KB alias for AutoTypeInjector.CompleteName, same pattern as the two above.
        internal static KbHandle? GetCurrentKb() => _currentKb.Value;

        // Tools that are not KB-scoped: routed by the gateway itself or operate on global state.
        // Must mirror the exclusion list in tool_definitions.json (no `kb` param on these).
        private static readonly HashSet<string> _metaTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "genexus_kb", "genexus_whoami", "genexus_logs", "genexus_doc", "genexus_worker_reload", "genexus_recipe"
        };
        private static bool IsMetaTool(string name) => _metaTools.Contains(name);

        internal static bool IsJsonRpcNotification(JObject request)
        {
            return request["id"] == null || request["id"]!.Type == JTokenType.Null;
        }
        private sealed class PendingWorkerRequest
        {
            public TaskCompletionSource<string> CompletionSource { get; init; } = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            public string ToolName { get; init; } = "unknown";
            public string CorrelationId { get; init; } = string.Empty;
            public string? OperationId { get; init; }
            public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
            /// <summary>Worker (KB) the command was routed to; used to abort pending on per-worker crash.</summary>
            public string? WorkerAlias { get; init; }
            // PERFORMANCE (perf-review): parsed response envelope. WorkerProcess already
            // parses every line to route it (notifications vs responses + in-flight
            // bookkeeping); HandleWorkerResponse stashes the JObject here so the await
            // sites in SendWorkerCommandAsync don't re-parse the raw json — this was
            // 3 full JObject.Parse per response, now 1. Large search/read responses are
            // exactly the ones that make the extra parses expensive.
            public JObject? ParsedResponse { get; set; }
        }

        private static ConcurrentDictionary<string, PendingWorkerRequest> _pendingRequests = new ConcurrentDictionary<string, PendingWorkerRequest>();
        private static ConcurrentDictionary<string, JObject> _semanticCache = new ConcurrentDictionary<string, JObject>();
        private static HttpSessionRegistry _httpSessions = new HttpSessionRegistry(TimeSpan.FromMinutes(10));
        private static IdempotencyCache _idempotencyCache = new IdempotencyCache(15, 1000);
        private static readonly OperationTracker _operationTracker = new OperationTracker(TimeSpan.FromMinutes(60));
        private static readonly MutationRecoveryRegistry _mutationRecovery = new MutationRecoveryRegistry();
        internal static OperationTracker OperationTracker => _operationTracker;

        // User-macro storage: <configRoot>/recipes/user-macros/<name>.json.
        // Same configRoot used by sandboxes (Configuration.CurrentConfigPath dir,
        // falling back to AppContext.BaseDirectory).
        internal static string GetUserMacroDir()
        {
            string configDir = !string.IsNullOrEmpty(Configuration.CurrentConfigPath)
                ? System.IO.Path.GetDirectoryName(Configuration.CurrentConfigPath!)!
                : AppContext.BaseDirectory;
            return System.IO.Path.Combine(configDir, "recipes", "user-macros");
        }
        internal static BackgroundJobRegistry JobRegistry = new BackgroundJobRegistry(600);
        private static int _workerWarmupStarted;
        private static int _indexBootstrapStarted;
        // v2.6.8 (review C6): incremented before any planned worker exit
        // (worker_reload, KB switch, shutdown) so OnWorkerExited can skip the
        // eager respawn — RestartWorker is already orchestrating a fresh spawn.
        // Refcounted so concurrent planned exits don't race.
        private static int _plannedExitSuppression = 0;
        private sealed class RespawnSuppressionScope : IDisposable
        {
            public void Dispose() => System.Threading.Interlocked.Decrement(ref _plannedExitSuppression);
        }
        internal static IDisposable SuppressEagerRespawn()
        {
            System.Threading.Interlocked.Increment(ref _plannedExitSuppression);
            return new RespawnSuppressionScope();
        }
        private static bool IsEagerRespawnSuppressed() =>
            System.Threading.Volatile.Read(ref _plannedExitSuppression) > 0;

        // issue #26 P1: last eager-respawn failure per KB alias. When eager respawn
        // exhausts its retries, we record (time, error) here so whoami/health can report
        // an honest "respawn_failed" with the real cause + a recovery hint, instead of a
        // perpetual, misleading "respawning" while no process is actually coming up.
        private static readonly ConcurrentDictionary<string, (DateTime AtUtc, string Error)> _respawnFailures =
            new ConcurrentDictionary<string, (DateTime, string)>(StringComparer.OrdinalIgnoreCase);
        private static bool _stdioActive;
        // #3: the client request that triggered a proxy→master promotion, buffered so the new
        // master can replay it once instead of dropping it across the takeover.
        private static string? _promotionReplayLine;
        // issue #43 #6: the client's initialize line, persisted across proxy RE-ENTRIES so a
        // session lost mid-conversation (e.g. a slow worker_reload timing the proxy out) can be
        // re-handshaked instead of wedging the whole server on "Master error: BadRequest".
        private static string? _proxyCachedInitializeLine;
        private static readonly TimeSpan _pendingRequestRetention = TimeSpan.FromMinutes(65);
        // issue #40: never keep the log handle open inside node_modules — that makes
        // `npx genexus-mcp@latest` fail with EBUSY on Windows when it refreshes the package.
        // Relocate to a stable per-user dir when the exe is under node_modules (honours
        // GXMCP_LOG_DIR). Dev/source/test builds keep the log next to the exe.
        private static readonly string _logDir = ResolveLogDirectory();
        private static readonly string _logPath = Path.Combine(_logDir, "gateway_debug.log");

        private static string ResolveLogDirectory()
        {
            try
            {
                var env = Environment.GetEnvironmentVariable("GXMCP_LOG_DIR");
                if (!string.IsNullOrWhiteSpace(env)) { Directory.CreateDirectory(env); return env; }
                string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
                if (baseDir.Replace('/', '\\').IndexOf("\\node_modules\\", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string dir = Path.Combine(local, "GenexusMCP", "logs");
                    Directory.CreateDirectory(dir);
                    return dir;
                }
                return baseDir;
            }
            catch { return AppDomain.CurrentDomain.BaseDirectory ?? ""; }
        }
        // Rotation: when the log exceeds this many bytes the current file is renamed to
        // gateway_debug.log.1 and a fresh file is opened.  Only two files are kept.
        private const long _logRotateBytes = 10 * 1024 * 1024; // 10 MB
        private static StreamWriter? _logWriter;
        private static readonly string[] _defaultLocalOrigins = new[]
        {
            "http://localhost",
            "http://127.0.0.1",
            "https://localhost",
            "https://127.0.0.1"
        };

        private static readonly object _logLock = new object();
        private static readonly System.Threading.SemaphoreSlim _stdoutGate = new System.Threading.SemaphoreSlim(1, 1);
        private static Configuration? _activeConfig;
        internal static Configuration? ActiveConfig => _activeConfig;

        public static void TryWriteStderr(string message)
        {
            Log(message);
        }

        public static async Task TryWriteStdout(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;
            try {
                await _stdoutGate.WaitAsync().ConfigureAwait(false);
                try {
                    await Console.Out.WriteLineAsync(msg);
                    await Console.Out.FlushAsync();
                } finally {
                    _stdoutGate.Release();
                }
            } catch { }
        }

        private static void InitializeLogging()
        {
            try
            {
                lock (_logLock)
                {
                    _logWriter = new StreamWriter(_logPath, append: true, System.Text.Encoding.UTF8) { AutoFlush = true };
                }
            }
            catch { /* fall back to no-op if the file is unavailable */ }
            Log("=== Gateway starting (Stdio Mode) ===");
        }

        // PERFORMANCE (perf-review): per-request instrumentation logs ([Cache] HIT,
        // [Cache] Invalidation, [TOOL-LATENCY]) each pay DateTime.Now formatting + a
        // lock + an AutoFlush disk write — measurable contention on high-throughput
        // pipelines. Default ON (preserves existing behavior and the diagnostics
        // scripts that grep [TOOL-LATENCY]); set GXMCP_VERBOSE_LOGS=0 to drop the
        // per-request noise. Cold-start / lifecycle / error logs are unaffected.
        internal static readonly bool _verboseRequestLogs =
            !string.Equals(Environment.GetEnvironmentVariable("GXMCP_VERBOSE_LOGS"), "0", StringComparison.OrdinalIgnoreCase);

        public static void Log(string msg)
        {
            try {
                lock (_logLock) {
                    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\n";
                    if (_logWriter == null)
                    {
                        // InitializeLogging() has not been called (e.g. in tests).
                        // Fall back to the direct append-all path so the file is
                        // always written and existing tests that snapshot file length
                        // continue to work.
                        File.AppendAllText(_logPath, line);
                        return;
                    }
                    // Size-based rotation: rename current to .1 (overwriting any previous .1)
                    // then open a fresh log file.
                    try
                    {
                        var fi = new FileInfo(_logPath);
                        if (fi.Exists && fi.Length > _logRotateBytes)
                        {
                            _logWriter.Dispose();
                            _logWriter = null;
                            string rotated = _logPath + ".1";
                            if (File.Exists(rotated)) File.Delete(rotated);
                            File.Move(_logPath, rotated);
                            _logWriter = new StreamWriter(_logPath, append: false, System.Text.Encoding.UTF8) { AutoFlush = true };
                        }
                    }
                    catch { /* rotation failure is non-fatal; keep using existing writer */ }
                    _logWriter?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}");
                }
            } catch { }
        }


        private static async Task RunSelfTestAndExitAsync()
        {
            var result = new JObject();
            var checks = new JArray();
            int failCount = 0;
            int warnCount = 0;

            void AddCheck(string id, string status, string detail)
            {
                if (status == "fail") failCount++;
                else if (status == "warn") warnCount++;
                checks.Add(new JObject { ["id"] = id, ["status"] = status, ["detail"] = detail });
            }

            // 1. Gateway exe location (so callers see where the test ran from).
            string gatewayExe;
            try { gatewayExe = Process.GetCurrentProcess().MainModule?.FileName ?? AppContext.BaseDirectory; }
            catch { gatewayExe = AppContext.BaseDirectory; }
            result["gatewayExe"] = gatewayExe;

            // 2. Config load — surfaces missing GX_CONFIG_PATH and JSON parse errors.
            Configuration? config = null;
            try
            {
                config = Configuration.Load();
                AddCheck("config_load", "pass", $"config.json loaded from {Configuration.CurrentConfigPath ?? "<unknown>"}");
            }
            catch (Exception ex)
            {
                AddCheck("config_load", "fail", $"config.json load failed: {ex.Message}");
            }

            // 3. GeneXus installation.
            string? gxPath = config?.GeneXus?.InstallationPath;
            if (string.IsNullOrWhiteSpace(gxPath))
            {
                AddCheck("gx_installation", "fail", "GeneXus.InstallationPath is not set in config.json");
            }
            else
            {
                string exe = Path.Combine(gxPath, "genexus.exe");
                if (File.Exists(exe))
                    AddCheck("gx_installation", "pass", $"genexus.exe present at {gxPath}");
                else
                    AddCheck("gx_installation", "fail", $"genexus.exe NOT found at {gxPath} (config points here but it is missing)");
            }

            // 4. In-process build assembly — loadable means Stream D's build daemon works.
            if (!string.IsNullOrWhiteSpace(gxPath))
            {
                string dll = Path.Combine(gxPath, "Genexus.MsBuild.Tasks.dll");
                if (File.Exists(dll))
                    AddCheck("in_process_build_assembly", "pass", $"Genexus.MsBuild.Tasks.dll present ({new FileInfo(dll).Length / 1024} KB)");
                else
                    AddCheck("in_process_build_assembly", "warn", $"Genexus.MsBuild.Tasks.dll missing — build will fall back to MSBuild.exe spawn");
            }

            // 5. KB path(s).
            string? kbPath = config?.Environment?.KBPath;
            if (string.IsNullOrWhiteSpace(kbPath))
            {
                AddCheck("kb_path", "warn", "No KB path configured");
            }
            else if (!Directory.Exists(kbPath))
            {
                AddCheck("kb_path", "fail", $"Configured KB path does not exist: {kbPath}");
            }
            else
            {
                bool looksLikeKb = false;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(kbPath))
                    {
                        var name = Path.GetFileName(f).ToLowerInvariant();
                        if (name.EndsWith(".gxw") || name == "knowledgebase.connection") { looksLikeKb = true; break; }
                    }
                }
                catch { }
                AddCheck("kb_path", looksLikeKb ? "pass" : "warn",
                    looksLikeKb ? $"KB folder shape OK at {kbPath}" : $"KB path exists but no .gxw / KnowledgeBase.Connection found: {kbPath}");
            }

            result["checks"] = checks;
            result["summary"] = new JObject
            {
                ["pass"] = checks.Count - failCount - warnCount,
                ["warn"] = warnCount,
                ["fail"] = failCount,
                ["total"] = checks.Count
            };
            result["ok"] = failCount == 0;
            result["schemaVersion"] = "gateway-selftest/1";

            // Single JSON line on stdout so the PowerShell installer can ConvertFrom-Json it.
            await Console.Out.WriteLineAsync(result.ToString(Formatting.None));
            await Console.Out.FlushAsync();
            Environment.Exit(failCount == 0 ? 0 : 1);
        }

        public static async Task Main(string[] args)
        {
            // Short-circuit self-test before any I/O setup. The CLI installer calls this
            // to validate the install: it loads config, checks GeneXus install + KB
            // existence + the in-process build dll, and prints a single JSON line to
            // stdout. No worker is started, no HTTP listener is opened, no logs file
            // is created. Replaces the no-op `--axi-spawn-probe` flag the installer
            // used to call (which only verified that the exe could be launched).
            if (args != null && args.Length > 0 && (args[0] == "--self-test" || args[0] == "--axi-self-test"))
            {
                await RunSelfTestAndExitAsync();
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += async (s, e) => {
                string msg = $"[{DateTime.Now}] FATAL UNHANDLED: {e.ExceptionObject}\n";
                var errorObj = new { jsonrpc = "2.0", method = "notifications/message", @params = new { level = "error", logger = "gateway", data = msg } };
                await TryWriteStdout(Newtonsoft.Json.JsonConvert.SerializeObject(errorObj));
                try { File.AppendAllText("gateway_panic.log", msg); } catch { }
            };

            // Register encoding provider for Windows-1252 support in .NET 8
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            try
            {
                Console.InputEncoding = System.Text.Encoding.UTF8;
                Console.OutputEncoding = System.Text.Encoding.UTF8;
            }
            catch (IOException)
            {
                // Detached HTTP-only launches may not have a console handle.
            }

            InitializeLogging();

            // Squirrel-style: if a previous session staged a newer build, swap it in
            // now (before any worker is spawned or files are opened). Fail-safe — a
            // locked file or any error just leaves the install untouched and retries
            // next launch. No-op for npx-cache launches (managed-install only).
            SelfUpdater.ApplyStagedUpdateOnStartup();

            var config = Configuration.Load();
            _activeConfig = config;
            LogGeneXusVersionCheck(config);
            try { RecipeCatalog.ConfigureUserMacroDirectory(GetUserMacroDir()); }
            catch (Exception ex) { Log("[RecipeCatalog] User-macro discovery skipped: " + ex.Message); }
            Log("[Gateway] Startup orphan-kill disabled. Existing gateway reuse is handled by the extension client.");
            AppDomain.CurrentDomain.ProcessExit += (_, __) =>
            {
                try { _gatewayLifetime.Cancel(); } catch { }
                if (_activeConfig != null)
                {
                    GatewayProcessLease.ReleaseCurrentProcess(_activeConfig);
                }
            };

            var leaseRegistration = GatewayProcessLease.TryRegisterCurrentProcess(config);
            bool isMaster = leaseRegistration.Success;

            if (!isMaster)
            {
                if (leaseRegistration.IsDuplicate && leaseRegistration.Lease != null)
                {
                    Log($"[Gateway] existing_master_detected currentPid={Environment.ProcessId} masterPid={leaseRegistration.Lease.ProcessId}");
                    
                    if (leaseRegistration.Lease.HttpPort > 0)
                    {
                        int masterPort = leaseRegistration.Lease.HttpPort;
                        while (true)
                        {
                            bool shouldPromote = await RunMcpProxyAsync(leaseRegistration.Lease, config);
                            if (!shouldPromote) return;

                            // Defense-in-depth (#2): the proxy asked to promote because it saw
                            // the master as unresponsive. Before stealing the lease — which via
                            // port recovery would hard-kill whatever holds the port, tree and all —
                            // re-verify the master is really down. If it's still accepting
                            // connections this was a false alarm; stay a proxy rather than cause a
                            // split-brain that kills a live master's worker.
                            if (await IsPortListeningAsync(masterPort, 2000))
                            {
                                Log($"[Gateway] Promotion aborted — master on port {masterPort} still listening. Resuming proxy mode.");
                                await Task.Delay(1000);
                                continue;
                            }

                            Log("[Gateway] Starting promotion to Master...");
                            var forced = GatewayProcessLease.ForceRegisterCurrentProcess(config);
                            if (!forced.Success) {
                                Log("[Gateway] Promotion failed: lease acquisition blocked.");
                                return;
                            }
                            isMaster = true;
                            break;
                        }
                    }
                    else 
                    {
                        Log($"[Gateway] Existing master (PID {leaseRegistration.Lease.ProcessId}) has no HTTP port. Reusing or exiting.");
                        return;
                    }
                }
                else 
                {
                    Log($"[Gateway] Registration failed: {leaseRegistration.FailureReason}");
                    return;
                }
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                Log("FATAL UNHANDLED EXCEPTION: " + (e.ExceptionObject as Exception)?.ToString());
            };

            TaskScheduler.UnobservedTaskException += (s, e) => {
                Log("UNOBSERVED TASK EXCEPTION: " + e.Exception?.ToString());
                e.SetObserved();
            };

            Log("=== Gateway starting (Stdio Mode) ===");
            
            _httpSessions = new HttpSessionRegistry(TimeSpan.FromMinutes(config.Server?.SessionIdleTimeoutMinutes ?? 10));
            _idempotencyCache = new IdempotencyCache(
                config.Server?.IdempotencyTtlMinutes ?? 15,
                config.Server?.IdempotencyCacheSize ?? 1000);
            
            // Subscribing to Configuration Changes
            Configuration.OnConfigurationChanged += (newConfig) => {
                if (newConfig.Environment?.KBPath != config.Environment?.KBPath || 
                    newConfig.GeneXus?.InstallationPath != config.GeneXus?.InstallationPath ||
                    newConfig.Environment?.GX_SHADOW_PATH != config.Environment?.GX_SHADOW_PATH ||
                    newConfig.Server?.HttpPort != config.Server?.HttpPort ||
                    newConfig.Server?.WorkerIdleTimeoutMinutes != config.Server?.WorkerIdleTimeoutMinutes) {
                    Log($"[Gateway] Core configuration changed! Restarting Worker process...");
                    config = newConfig; // Update reference
                    _activeConfig = config;
                    GatewayProcessLease.RefreshCurrentProcess(config);
                    RestartWorker(config);
                    BroadcastResourcesListChanged("core_configuration_changed");
                } else {
                    Log($"[Gateway] Minor configuration changed. Ignoring.");
                }
            };

            // 1. Start HTTP Server first (it's critical for VS Code communication)
            if (config.Server?.HttpPort > 0)
            {
                Log($"[Gateway] Starting HTTP server on port {config.Server.HttpPort}...");
                _ = Task.Run(async () => {
                    int retryCount = 0;
                    while (retryCount < 5) {
                        try { 
                            await StartHttpServer(config); 
                            Log("[Gateway] HTTP server bound and active.");
                            while(true) {
                                await Task.Delay(30000);
                                Log("[Gateway] Heartbeat: HTTP server still active.");
                            }
                        }
                        catch (Exception exHttp) { 
                            Log($"[HTTP] Bind failure (5000): {exHttp.Message}. Attempting port recovery ({retryCount + 1}/5)...");
                            TryKillProcessOnPort(config.Server?.HttpPort ?? 5000);
                            retryCount++;
                            await Task.Delay(1000);
                        }
                    }
                });
            }

            // 2. Start Worker in background
            Log("[Gateway] Initializing Worker lifecycle...");
            StartWorker(config);
            Log("[Gateway] Worker lifecycle ready.");

            // 3. Subscribing to KB changes for Semantic Cache Invalidation
            if (!string.IsNullOrEmpty(config.Environment?.KBPath))
            {
                Log("[Gateway] Setting up .gx_mirror watcher...");
                try 
                {
                    string mirrorPath = Path.Combine(config.Environment.KBPath, ".gx_mirror");
                    if (!Directory.Exists(mirrorPath)) Directory.CreateDirectory(mirrorPath);
                    var watcher = new FileSystemWatcher(mirrorPath) 
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                        EnableRaisingEvents = true
                    };
                    watcher.Changed += (s, e) => {
                        Log($"[Cache] Invalidation triggered by external change: {e.Name}");
                        _semanticCache.Clear();
                        BroadcastResourceUpdated("genexus://objects", "external_kb_change");
                    };
                    Log("[Gateway] .gx_mirror watcher active.");
                } catch (Exception ex) { Log($"[Cache] Watcher error: {ex.Message}"); }
            }

            if (config.Server?.McpStdio == true)
            {
                Log("[Gateway] Entering Stdio Loop...");
                _stdioActive = true;
                var reader = Console.In;

                // #3: replay the request that triggered a promotion (see RunMcpProxyAsync).
                // It already parsed as JSON in the proxy, so process it through the normal
                // path once and emit its response, so the client's call isn't lost across the
                // proxy→master takeover.
                var replayLine = System.Threading.Interlocked.Exchange(ref _promotionReplayLine, null);
                if (!string.IsNullOrWhiteSpace(replayLine) && replayLine!.Trim().StartsWith("{"))
                {
                    Log("[Gateway] Replaying the request that triggered promotion.");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var req = JObject.Parse(replayLine);
                            var resp = await ProcessMcpRequest(req);
                            if (resp != null && !IsJsonRpcNotification(req))
                                await TryWriteStdout(resp.ToString(Formatting.None));
                        }
                        catch (Exception ex) { Log("[Gateway] Promotion replay failed: " + ex.Message); }
                    });
                }
                while (true)
                {
                    string? line = null;
                    try { line = await reader.ReadLineAsync(); } catch { }

                    if (line == null)
                    {
                        if (config.Server?.HttpPort > 0)
                        {
                            Log("Stdio closed, keeping alive for HTTP...");
                            await Task.Delay(-1);
                        }
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (!line.Trim().StartsWith("{")) {
                        Log($"[Protocol] Ignored non-JSON noise on stdin: {line}");
                        continue;
                    }

                    // Dispatch each request concurrently so a slow tool call (worker
                    // cold-start, index build, edit reapply, self-refresh) can't park
                    // the read loop and starve the host's keepalive `ping` — the symptom
                    // behind the IDE's "MCP Server parou de responder" popup while idle.
                    // JSON-RPC correlates responses by id, so out-of-order replies are
                    // spec-legal; TryWriteStdout serializes writes via _stdoutGate and
                    // _currentKb is AsyncLocal, so each dispatched request keeps its own
                    // KB routing.
                    string capturedLine = line;
                    _ = Task.Run(async () =>
                    {
                        JToken? capturedId = null;
                        try
                        {
                            JObject request;
                            try
                            {
                                request = JObject.Parse(capturedLine);
                            }
                            catch (Exception parseEx)
                            {
                                Log("MCP parse error: " + parseEx.Message);
                                var parseErr = new JObject
                                {
                                    ["jsonrpc"] = "2.0",
                                    ["id"] = JValue.CreateNull(),
                                    ["error"] = new JObject { ["code"] = -32700, ["message"] = "Parse error" }
                                };
                                await TryWriteStdout(parseErr.ToString(Formatting.None));
                                return;
                            }
                            capturedId = request["id"];
                            bool notification = IsJsonRpcNotification(request);
                            var response = await ProcessMcpRequest(request);
                            if (response != null && !notification)
                            {
                                await TryWriteStdout(response.ToString(Formatting.None));
                            }
                        }
                        catch (WorkerPoolFullException poolEx)
                        {
                            Log("MCP WorkerPoolFull: " + poolEx.Message);
                            var errResp = new JObject
                            {
                                ["jsonrpc"] = "2.0",
                                ["id"] = capturedId?.DeepClone() ?? JValue.CreateNull(),
                                ["error"] = new JObject { ["code"] = -32000, ["message"] = poolEx.Message }
                            };
                            if (capturedId != null && capturedId.Type != JTokenType.Null)
                                await TryWriteStdout(errResp.ToString(Formatting.None));
                        }
                        catch (Exception ex)
                        {
                            Log("MCP Error: " + ex.Message);
                            if (capturedId != null && capturedId.Type != JTokenType.Null)
                            {
                                var errResp = new JObject
                                {
                                    ["jsonrpc"] = "2.0",
                                    ["id"] = capturedId.DeepClone(),
                                    ["error"] = new JObject { ["code"] = -32603, ["message"] = "Internal error" }
                                };
                                await TryWriteStdout(errResp.ToString(Formatting.None));
                            }
                        }
                    });
                }
            }
            else if (config.Server?.HttpPort > 0)
            {
                Log("[Gateway] MCP stdio disabled. Serving HTTP only.");
                await Task.Delay(-1);
            }
        }

        // An empty proxy→master response body is legitimate (not a dead master) when the
        // request was a JSON-RPC notification (no id → no response expected) OR the master
        // explicitly returned HTTP 204 No Content. Treating those as failures was the trigger
        // for false "Master unresponsive" promotions that then tree-killed the live master +
        // its worker. Only an id-bearing request that gets an empty 200 is a real fault.
        internal static bool ProxyEmptyBodyIsSuccess(bool isNotification, System.Net.HttpStatusCode status)
            => isNotification || status == System.Net.HttpStatusCode.NoContent;

        // Cheap liveness probe: does anything accept a TCP connection on the port right now?
        // Used before a forced promotion so a transient hiccup can't make a proxy steal the
        // lease from — and then kill — a master that is plainly still up. A connection refusal
        // (master really gone) throws and returns false.
        private static async Task<bool> IsPortListeningAsync(int port, int timeoutMs)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var connectTask = client.ConnectAsync("127.0.0.1", port);
                var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));
                if (completed != connectTask) return false; // timed out — treat as down
                await connectTask; // observe exceptions (connection refused → caught below)
                return client.Connected;
            }
            catch { return false; }
        }

        private static async Task<bool> RunMcpProxyAsync(GatewayLeaseRecord master, Configuration config)
        {
            string baseUrl = $"http://localhost:{master.HttpPort}/mcp";
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30); // Do not let proxy hang forever if master is dead
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
            
            string? sessionId = null;
            // issue #38 defect #3: cache the client's `initialize` line so a dropped/expired
            // master session can be re-established transparently (replay initialize → fresh
            // session → resend the failed request) instead of relaying "Master error: NotFound"
            // to the client forever.
            // issue #43 #6: seed from the static cache so the initialize survives a proxy
            // RE-ENTRY. When a slow reload makes the proxy time out and loop back into a fresh
            // RunMcpProxyAsync (sessionId reset to null), a mid-session client will not resend
            // initialize — without the persisted copy we could never re-handshake and every call
            // 400'd ("Missing MCP-Session-Id") forever until a full client restart.
            string? cachedInitializeLine = _proxyCachedInitializeLine;
            string negotiatedProtocolVersion = ResolveProxyProtocolVersion(cachedInitializeLine);
            var reader = Console.In;
            var cts = new CancellationTokenSource();
            var ct = cts.Token;

            Log($"[Proxy] Proxy mode active (Master PID {master.ProcessId} on port {master.HttpPort}).");

            while (true)
            {
                string? line = null;
                try { line = await reader.ReadLineAsync(ct); } catch { break; }
                if (line == null) break;

                int retryCount = 0;
                bool success = false;
                while (retryCount < 3 && !success)
                {
                    try
                    {
                        string body = line;
                        var request = JObject.Parse(body);
                        string requestId = request["id"]?.ToString() ?? "unknown";
                        bool isInitialize = string.Equals(request["method"]?.ToString(), "initialize", StringComparison.Ordinal);
                        bool isModern = McpRouter.IsModernRequest(request);
                        string requestProtocolVersion = McpHttpProtocol.GetRequestProtocolVersion(request)
                            ?? negotiatedProtocolVersion;
                        // A JSON-RPC notification has no id and expects NO response — the
                        // master answers it with HTTP 204/empty, which is correct, not a fault.
                        bool isNotification = request["id"] == null || request["id"]!.Type == JTokenType.Null;
                        // Remember the initialize handshake so we can replay it if the master
                        // session later expires (issue #38 defect #3).
                        if (isInitialize && !isModern)
                        {
                            cachedInitializeLine = line;
                            _proxyCachedInitializeLine = line; // survive proxy re-entry (issue #43 #6)
                            negotiatedProtocolVersion = McpRouter.NegotiateProtocolVersion(
                                (request["params"] as JObject)?["protocolVersion"]?.ToString());
                        }
                        var content = new StringContent(body, Encoding.UTF8, "application/json");
                        
                        if (!isModern && sessionId != null) content.Headers.Add("MCP-Session-Id", sessionId);

                        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, baseUrl) { Content = content };
                        requestMessage.Headers.Add("MCP-Protocol-Version", requestProtocolVersion);
                        if (!isModern && sessionId != null) requestMessage.Headers.Add("MCP-Session-Id", sessionId);
                        if (isModern)
                        {
                            string method = request["method"]?.ToString() ?? string.Empty;
                            requestMessage.Headers.Add("Mcp-Method", method);
                            string? headerName = McpHttpProtocol.GetStandardHeaderName(request);
                            if (headerName != null)
                                requestMessage.Headers.Add("Mcp-Name", McpHttpProtocol.EncodeHeaderValue(headerName));
                        }

                        var response = await httpClient.SendAsync(requestMessage, ct);
                        
                        if (!isModern && sessionId == null && response.Headers.TryGetValues("MCP-Session-Id", out var values))
                        {
                            sessionId = values.FirstOrDefault();
                            if (sessionId != null)
                            {
                                Log($"[Proxy] Handshake complete. ID: {sessionId}");
                                // Wait a moment for master to stabilize before streaming notifications
                                await Task.Delay(2000);
                                _ = Task.Run(() => RunProxySseForwarderAsync(master.HttpPort, sessionId, negotiatedProtocolVersion, cts.Token));
                            }
                        }

                        if (response.IsSuccessStatusCode)
                        {
                            string responseBody = await response.Content.ReadAsStringAsync(ct);
                            if (string.IsNullOrWhiteSpace(responseBody))
                            {
                                // Empty body on a notification (or an explicit 204) is the
                                // spec-correct "accepted, no response" — NOT a dead master.
                                // Reading it as failure here was the trigger for a false
                                // "Master unresponsive" → forced promotion → the promoted
                                // gateway's port-recovery then hard-killed the real master's
                                // whole process tree, GeneXus worker included. Every MCP
                                // client sends id-less notifications routinely, so this fired
                                // on ordinary traffic. Only an id-bearing REQUEST that gets no
                                // body is a genuine fault worth retrying/promoting on.
                                if (ProxyEmptyBodyIsSuccess(isNotification, response.StatusCode))
                                {
                                    success = true;
                                }
                                else
                                {
                                    Log($"[Proxy] Master returned empty response for request {requestId}. Retrying...");
                                    throw new Exception("Empty response from master");
                                }
                            }
                            else if (!responseBody.Trim().StartsWith("{"))
                            {
                                Log($"[Proxy] Master returned non-JSON response: {responseBody}");
                                throw new Exception("Invalid response from master");
                            }
                            else
                            {
                                await TryWriteStdout(responseBody);
                                success = true;
                            }
                        }
                        else
                        {
                            string remoteError = await response.Content.ReadAsStringAsync(ct);

                            // issue #38 defect #3: a 404 from a LIVE master (it answered) means our
                            // MCP session expired or the master restarted with a fresh session store
                            // — NOT that the master is dead. Relaying "Master error: NotFound" to the
                            // client left it permanently wedged (every later call 404'd, worker_reload
                            // included). Re-establish a session by replaying the cached initialize, then
                            // resend the original request. This is distinct from the connection-failure
                            // promotion path below, which must NOT fire while the master is alive.
                            if (!isModern && response.StatusCode == System.Net.HttpStatusCode.NotFound
                                && sessionId != null && !isNotification)
                            {
                                Log($"[Proxy] Master 404 (session {sessionId} expired/unknown). Re-initializing session...");
                                sessionId = null;
                                string? newSessionId = await ProxyRehandshakeAsync(httpClient, baseUrl, cachedInitializeLine, negotiatedProtocolVersion, ct);
                                if (newSessionId != null)
                                {
                                    sessionId = newSessionId;
                                    Log($"[Proxy] Re-handshake complete. New ID: {sessionId}");
                                    _ = Task.Run(() => RunProxySseForwarderAsync(master.HttpPort, sessionId, negotiatedProtocolVersion, cts.Token));
                                    retryCount++;
                                    continue; // resend the original request with the fresh session
                                }
                                Log("[Proxy] Re-handshake failed (no cached initialize or master refused); returning error to client.");
                            }

                            // issue #43 #6: a 400 "Missing MCP-Session-Id" from a LIVE master means our
                            // session was lost — typically after a slow worker_reload made the proxy time
                            // out and re-enter RunMcpProxyAsync with sessionId=null. The master rejects
                            // every non-initialize call without a session header, so the whole server used
                            // to return "Master error: BadRequest" forever until a full client restart.
                            // Recover exactly like the 404 case: replay the (persisted) initialize to mint a
                            // fresh session, then resend the original request. Gated on the session-missing
                            // signal so a genuine bad-request 400 still surfaces to the client.
                            if (!isModern && response.StatusCode == System.Net.HttpStatusCode.BadRequest
                                && !isNotification
                                && !string.IsNullOrEmpty(cachedInitializeLine)
                                && remoteError != null
                                && remoteError.IndexOf("MCP-Session-Id", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                Log("[Proxy] Master 400 (session missing). Re-initializing session...");
                                sessionId = null;
                                string? newSessionId = await ProxyRehandshakeAsync(httpClient, baseUrl, cachedInitializeLine, negotiatedProtocolVersion, ct);
                                if (newSessionId != null)
                                {
                                    sessionId = newSessionId;
                                    Log($"[Proxy] Re-handshake complete (after 400). New ID: {sessionId}");
                                    _ = Task.Run(() => RunProxySseForwarderAsync(master.HttpPort, sessionId, negotiatedProtocolVersion, cts.Token));
                                    retryCount++;
                                    continue; // resend the original request with the fresh session
                                }
                                Log("[Proxy] Re-handshake after 400 failed; returning error to client.");
                            }

                            // Modern transport errors are already JSON-RPC responses. Preserve
                            // their protocol error code/data instead of flattening them into a
                            // transport-shaped "Master error: BadRequest" envelope.
                            if (isModern && !isNotification)
                            {
                                try
                                {
                                    var jsonError = JObject.Parse(remoteError ?? string.Empty);
                                    if (jsonError["jsonrpc"] != null && jsonError["error"] != null)
                                    {
                                        await TryWriteStdout(jsonError.ToString(Formatting.None));
                                        success = true;
                                        continue;
                                    }
                                }
                                catch { /* fall through to the generic proxy error */ }
                            }

                            Log($"[Proxy] Master status {response.StatusCode}: {remoteError}");
                            var id = request["id"];
                            if (id != null)
                            {
                                var errorResponse = new JObject
                                {
                                    ["jsonrpc"] = "2.0",
                                    ["id"] = id.DeepClone(),
                                    ["error"] = new JObject { ["code"] = (int)response.StatusCode, ["message"] = $"Master error: {response.StatusCode}" }
                                };
                                await TryWriteStdout(errorResponse.ToString(Formatting.None));
                            }
                            success = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        retryCount++;
                        Log($"[Proxy] Connection failed to Master ({retryCount}/3): {ex.Message}");
                        if (retryCount >= 3)
                        {
                            Log("[Proxy] Master unresponsive. Triggering promotion...");
                            // Buffer the request that triggered promotion so the newly-promoted
                            // master can replay it — otherwise this one client call is silently
                            // lost across the takeover (it was read off stdin and never answered).
                            _promotionReplayLine = line;
                            return true;
                        }
                        await Task.Delay(1000);
                    }
                }
            }
            cts.Cancel();
            Log("[Proxy] Stdio closed.");
            return false;
        }

        // issue #38 defect #3: replay the cached initialize against the master to obtain a
        // fresh MCP session after the previous one expired/was dropped. Returns the new
        // session id, or null when there is nothing to replay or the master refused. The
        // initialize response body is intentionally discarded — the client already received
        // its initialize reply; this handshake is an internal session refresh.
        private static async Task<string?> ProxyRehandshakeAsync(HttpClient httpClient, string baseUrl, string? cachedInitializeLine, string protocolVersion, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(cachedInitializeLine)) return null;
            try
            {
                var content = new StringContent(cachedInitializeLine!, Encoding.UTF8, "application/json");
                using var msg = new HttpRequestMessage(HttpMethod.Post, baseUrl) { Content = content };
                msg.Headers.Add("MCP-Protocol-Version", protocolVersion);
                var resp = await httpClient.SendAsync(msg, ct);
                if (resp.IsSuccessStatusCode && resp.Headers.TryGetValues("MCP-Session-Id", out var values))
                    return values.FirstOrDefault();
                Log($"[Proxy] Re-handshake POST returned {(int)resp.StatusCode} with no session header.");
            }
            catch (Exception ex) { Log($"[Proxy] Re-handshake error: {ex.Message}"); }
            return null;
        }

        private static string ResolveProxyProtocolVersion(string? initializeLine)
        {
            try
            {
                var request = JObject.Parse(initializeLine ?? string.Empty);
                return McpRouter.NegotiateProtocolVersion(McpHttpProtocol.GetRequestProtocolVersion(request));
            }
            catch
            {
                return McpRouter.SupportedProtocolVersion;
            }
        }

        private static async Task RunProxySseForwarderAsync(int port, string sessionId, string protocolVersion, CancellationToken ct)
        {
            string url = $"http://localhost:{port}/mcp";
            using var client = new HttpClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.Add("MCP-Session-Id", sessionId);
            client.DefaultRequestHeaders.Add("MCP-Protocol-Version", protocolVersion);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

            try
            {
                Log("[Proxy-SSE] Notification link active.");
                using var stream = await client.GetStreamAsync(url, ct);
                using var reader = new StreamReader(stream);
                while (!ct.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(ct);
                    if (line == null) break;

                    if (line.StartsWith("data: "))
                    {
                        string data = line.Substring(6).Trim();
                        // SSE message events (notifications) usually come in formatted as data blocks
                        // We must enforce jsonrpc wrapper to avoid client parsing errors on metadata like {"sessionId":"..."}
                        if (data.StartsWith("{") && data.EndsWith("}") && data.Contains("\"jsonrpc\""))
                        {
                            await TryWriteStdout(data);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"[Proxy-SSE] Background channel error: {ex.Message}");
            }
        }


    }
}
