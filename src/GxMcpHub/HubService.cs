using System.Text.Json.Nodes;

namespace GxMcpHub;

internal sealed class HubService : IAsyncDisposable
{
    // Set to 0, false, off or no in the environment of the Hub to keep the 0.1.1 behaviour: nothing is reloaded,
    // reconnected or started unless gxhub_refresh is called.
    internal const string AutoReconcileVariable = "GXMCP_HUB_AUTO_RECONCILE";

    // Set to 0, false, off or no to keep the behaviour before 0.3.0 on the way out: the Gateway this Hub started is
    // always stopped, even with other Hubs connected to it. The presence of this Hub is registered all the same.
    internal const string KeepSharedGatewayVariable = "GXMCP_HUB_KEEP_SHARED_GATEWAY";

    // Set to 1, true, on or yes to leave the Gateway this Hub started running when the Hub leaves with no other Hub
    // connected to it (0.4.0 stops it then), so that the next Hub to connect finds the Worker warm: the Worker leaves by
    // itself after WorkerIdleTimeoutMinutes, and a Gateway left like this lasts until the next promotion or a restart.
    // Off by default. The switch above wins: with GXMCP_HUB_KEEP_SHARED_GATEWAY=0 the Gateway is always stopped.
    internal const string KeepGatewayWhenAloneVariable = "GXMCP_HUB_KEEP_GATEWAY_WHEN_ALONE";
    internal const string DeferredByOwnGateway = "gateway-started-by-this-hub-is-running";
    internal const string ProfileChangedCode = "profile-changed-on-disk";
    internal const string ProfileReloadDeferredCode = "profile-reload-deferred";
    internal const string DeferredToManualRefresh = "change-requires-manual-refresh";
    internal const string BackendOfflineCode = "backend-offline";
    private const int MaxStartRounds = 6;
    internal static readonly TimeSpan DefaultReconcileInterval = TimeSpan.FromSeconds(10);

    private LoadedHubConfig _loaded;
    private Dictionary<string, BackendRuntime> _backends;
    private readonly WriterLease? _writerLease;
    private readonly Func<HubConfig, BackendConfig, BackendRuntime> _backendFactory;
    private readonly Func<string, LoadedHubConfig> _profileLoader;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _mapGate = new();
    private readonly bool _autoReconcileEnabled;
    private readonly bool _keepSharedGateway;
    private readonly bool _keepGatewayWhenAlone;
    private readonly OutsideJobStarter _outsideJob;
    private readonly HubPresence _presence = new();
    private TimeSpan _presenceLeaseWait = TimeSpan.FromSeconds(2);
    private TimeSpan _presenceRetryInterval = TimeSpan.FromSeconds(10);
    private Dictionary<string, BackendTool> _toolMap = new(StringComparer.OrdinalIgnoreCase);
    private string _profileFingerprint;
    private DateTimeOffset? _lastProfileReloadAtUtc;
    private string? _lastProfileReloadTrigger;
    private volatile AutoReloadState _autoReload = AutoReloadState.Initial;
    private CancellationTokenSource? _reconcileCts;
    private Task? _reconcileLoop;
    private long _reconcileRoundsSkipped;
    private long _promotionLockChecks;
    private readonly object _profileWatchGate = new();
    private string _observedOnDiskFingerprint;
    private DateTime _observedOnDiskAtUtc;
    private DateTime? _profileEditNotBeforeUtc;
    private string? _lastReconcileFailure;

    public HubService(
        LoadedHubConfig loaded,
        Func<HubConfig, BackendConfig, BackendRuntime>? backendFactory = null,
        bool? autoReconcile = null,
        Func<string, LoadedHubConfig>? profileLoader = null,
        bool? keepSharedGateway = null,
        OutsideJobStarter? outsideJob = null,
        bool? keepGatewayWhenAlone = null)
    {
        _loaded = loaded;
        _backendFactory = backendFactory ?? ((hub, backend) => new BackendRuntime(hub, backend));
        // A parameter only so that the self-tests can make a reload fail the way a file in use or a loader defect
        // would.
        _profileLoader = profileLoader ?? HubConfigLoader.Load;
        _autoReconcileEnabled = autoReconcile ?? ParseAutoReconcile(
            Environment.GetEnvironmentVariable(AutoReconcileVariable));
        _keepSharedGateway = keepSharedGateway ?? ParseAutoReconcile(
            Environment.GetEnvironmentVariable(KeepSharedGatewayVariable));
        _keepGatewayWhenAlone = keepGatewayWhenAlone ?? ParseOptIn(
            Environment.GetEnvironmentVariable(KeepGatewayWhenAloneVariable));
        // One decision for every backend of this Hub: leave the job of the client or not (GXMCP_HUB_START_OUTSIDE_JOB).
        _outsideJob = outsideJob ?? new OutsideJobStarter(OutsideJobPolicySetting.FromEnvironment());
        _writerLease = WriterLease.AcquireIfNeeded(loaded.Config);
        // The revision of what was loaded, not of what the file holds by now: when they differ, the profile was
        // rewritten after the load, and the first reconciliation or backend start reloads it.
        _profileFingerprint = loaded.Fingerprint;
        _observedOnDiskFingerprint = _profileFingerprint;
        _observedOnDiskAtUtc = DateTime.UtcNow;
        _backends = CreateBackends(loaded.Config, _profileFingerprint);
    }

    public event Action? ToolsChanged;

    public HubConfig Config => _loaded.Config;

    // Periodic rounds that found a tool call in flight and did nothing.
    internal long ReconcileRoundsSkipped => Interlocked.Read(ref _reconcileRoundsSkipped);

    internal bool BackgroundReconcileStopped => _reconcileLoop is null || _reconcileLoop.IsCompleted;

    // How long a tool call or gxhub_refresh waits for a promotion to finish before giving up on starting the backend,
    // and how often it looks. The Hub startup never waits.
    internal TimeSpan PromotionLockWait { get; set; } = TimeSpan.FromSeconds(120);

    internal TimeSpan PromotionLockPoll { get; set; } = TimeSpan.FromMilliseconds(500);

    // How long the Hub, on its way out, waits for the startup lease of an endpoint to ask whether another Hub is
    // connected to the Gateway it started. Short on purpose: without the answer the Gateway is left running.
    internal TimeSpan ExitLeaseWait { get; set; } = TimeSpan.FromSeconds(3);

    // How long a connection waits for the startup lease of the endpoint to register the presence of this Hub. When
    // the lease stays busy the Hub connects without being seen and tries again on the next round.
    internal TimeSpan PresenceLeaseWait
    {
        get => _presenceLeaseWait;
        set
        {
            _presenceLeaseWait = value;
            foreach (var backend in _backends.Values)
            {
                backend.PresenceLeaseWait = value;
            }
        }
    }

    // How long after a failed registration the presence is not tried again by a call or a round.
    internal TimeSpan PresenceRetryInterval
    {
        get => _presenceRetryInterval;
        set
        {
            _presenceRetryInterval = value;
            foreach (var backend in _backends.Values)
            {
                backend.PresenceRetryInterval = value;
            }
        }
    }

    // The presence of this Hub on the endpoints it is connected to.
    internal HubPresence Presence => _presence;

    // How many times the start path asked whether a promotion is in progress. The status probe is not counted.
    internal long PromotionLockChecks => Interlocked.Read(ref _promotionLockChecks);

    private string PromotionLockPath => PromotionLock.PathFor(Config);

    internal static bool ParseAutoReconcile(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is not ("0" or "false" or "off" or "no");
    }

    // The opposite default: on only for an explicit 1, true, on or yes. Unset, empty or anything else is off.
    internal static bool ParseOptIn(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "1" or "true" or "on" or "yes";
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(allowStartup: true, notify: false, cancellationToken);
    }

    // The periodic passive reconciliation. A round never starts a process and never waits behind a tool call: when
    // the gate is taken the round is skipped, and the next call reconciles before it is dispatched.
    public void StartBackgroundReconcile(TimeSpan? interval = null)
    {
        if (!_autoReconcileEnabled || _reconcileLoop is not null)
        {
            return;
        }

        var period = interval ?? DefaultReconcileInterval;
        _reconcileCts = new CancellationTokenSource();
        var token = _reconcileCts.Token;
        _reconcileLoop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(period);
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    try
                    {
                        await ReconcileAsync(fromTimer: true, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[hub] reconciliação periódica falhou: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Encerramento do Hub.
            }
        });
    }

    public IReadOnlyList<JsonObject> GetExposedToolDefinitions()
    {
        var result = MetaToolDefinitions().ToList();
        BackendTool[] tools;
        lock (_mapGate)
        {
            tools = _toolMap.Values.OrderBy(tool => tool.ExposedName, StringComparer.Ordinal).ToArray();
        }

        foreach (var tool in tools)
        {
            var definition = (JsonObject)tool.Definition.DeepClone();
            definition["name"] = tool.ExposedName;
            var description = definition["description"]?.GetValue<string>() ?? string.Empty;
            definition["description"] = $"[{tool.BackendId}] {description}";
            result.Add(definition);
        }

        return result;
    }

    public async Task<JsonNode?> CallAsync(
        string exposedToolName,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        switch (exposedToolName)
        {
            case "gxhub_status":
                await ReconcileAsync(fromTimer: false, cancellationToken);
                await ProbeBackendsAsync(notify: true, cancellationToken);
                return ToolText(StatusJson().ToJsonString());
            case "gxhub_list_tools":
                await ReconcileAsync(fromTimer: false, cancellationToken);
                await ProbeBackendsAsync(notify: true, cancellationToken);
                return ToolText(ListToolsJson(arguments).ToJsonString());
            case "gxhub_refresh":
            {
                var refusal = await RefreshFromProfileAsync(allowStartup: true, notify: true, cancellationToken);
                return refusal is null
                    ? ToolText(StatusJson().ToJsonString())
                    : ToolText(refusal, isError: true);
            }
            case "gxhub_call":
                return await GenericCallAsync(arguments, cancellationToken);
        }

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var changed = await ReconcileUnderGateAsync(cancellationToken);
            if (await EnsurePresenceAsync(cancellationToken))
            {
                changed = true;
            }

            BackendTool? tool = FindMappedTool(exposedToolName);
            if (tool is null && _autoReconcileEnabled)
            {
                var offline = _backends.Values.FirstOrDefault(candidate =>
                    candidate.Config.Enabled &&
                    !candidate.Online &&
                    exposedToolName.StartsWith(candidate.Config.ToolPrefix, StringComparison.OrdinalIgnoreCase));
                if (offline is not null)
                {
                    var startError = await StartOfflineBackendForCallAsync(offline, cancellationToken);
                    changed = true;
                    if (startError is not null)
                    {
                        ToolsChanged?.Invoke();
                        return ToolText(startError, isError: true);
                    }

                    tool = FindMappedTool(exposedToolName);
                }
            }

            if (changed)
            {
                ToolsChanged?.Invoke();
            }

            if (tool is null)
            {
                return ToolText($"Tool não encontrada ou bloqueada pela política: {exposedToolName}", isError: true);
            }

            var backend = _backends[tool.BackendId];
            if (!IsAllowed(backend, tool))
            {
                return PolicyBlocked(tool);
            }

            try
            {
                // Dispatched exactly once: a call that may have reached the backend is never sent again.
                return await backend.CallAsync(tool.OriginalName, arguments, cancellationToken)
                       ?? ToolText($"Backend '{backend.Config.Id}' devolveu result vazio.", isError: true);
            }
            catch (Exception ex)
            {
                return ToolText($"Falha no backend '{backend.Config.Id}': {ex.Message}", isError: true);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    // Before a call is dispatched, whether or not the reconciliation is on: the presence of this Hub is tried again
    // where it is missing (the startup lease was busy when it connected). True when a backend went offline because
    // the Gateway it was connected to is gone; the tool map is rebuilt.
    private async Task<bool> EnsurePresenceAsync(CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var backend in _backends.Values)
        {
            if (await backend.EnsurePresenceAsync(cancellationToken))
            {
                changed = true;
            }
        }

        if (changed)
        {
            RebuildToolMap();
        }

        return changed;
    }

    private BackendTool? FindMappedTool(string exposedToolName)
    {
        lock (_mapGate)
        {
            _toolMap.TryGetValue(exposedToolName, out var tool);
            return tool;
        }
    }

    // The only automatic path that may start a Gateway: a tool call that needs a backend which is offline. It is
    // initialized once, from the profile this Hub has loaded now and under the startup lease shared by every Hub,
    // before the call is dispatched. A failure is returned to the caller; it never takes the Hub down.
    private async Task<string?> StartOfflineBackendForCallAsync(
        BackendRuntime backend,
        CancellationToken cancellationToken)
    {
        var backendId = backend.Config.Id;
        try
        {
            var refusal = await InitializeAllowingStartAsync(backendId, StartMode.Call, cancellationToken);
            RebuildToolMap();
            if (refusal is not null)
            {
                return refusal;
            }

            // The profile may have been reloaded on the way: the backend is looked up again.
            if (_backends.TryGetValue(backendId, out var current) && current.Online)
            {
                return null;
            }

            return BackendOfflineCode + ": o backend não ficou disponível para esta chamada (" +
                   (current?.Error ?? "sem endpoint disponível") + ").";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RebuildToolMap();
            // The exception text carries the command path and the working directory: it stays in stderr. The caller
            // gets the code and the sentence the backend recorded, or the generic one.
            Console.Error.WriteLine(
                $"[hub] início sob demanda do backend '{backendId}' falhou: {ex.GetType().Name}: {ex.Message}");
            var recorded = _backends.TryGetValue(backendId, out var failed) ? failed.Error : null;
            return recorded is not null && recorded.StartsWith("backend-start-", StringComparison.Ordinal)
                ? recorded
                : ex is TimeoutException ? BackendRuntime.StartTimeoutText : BackendRuntime.StartFailedText;
        }
    }

    private async Task ProbeBackendsAsync(bool notify, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var tasks = _backends.Values.Select(backend => backend.ProbeAsync(cancellationToken));
            await Task.WhenAll(tasks);
            RebuildToolMap();
        }
        finally
        {
            _refreshGate.Release();
        }

        if (notify)
        {
            ToolsChanged?.Invoke();
        }
    }

    // Passive reconciliation, on the timer and before every tool call. It loads a profile that changed on disk when
    // that does not require stopping a Gateway this Hub started, and it connects again when the process behind an
    // endpoint was replaced. It never starts a process. Returns true when the tool map was rebuilt.
    internal async Task<bool> ReconcileAsync(bool fromTimer, CancellationToken cancellationToken)
    {
        if (fromTimer)
        {
            // A tool call holds the gate for as long as the backend takes: a periodic round never queues behind it.
            if (!await _refreshGate.WaitAsync(0, cancellationToken))
            {
                Interlocked.Increment(ref _reconcileRoundsSkipped);
                return false;
            }
        }
        else
        {
            await _refreshGate.WaitAsync(cancellationToken);
        }

        bool changed;
        try
        {
            changed = await ReconcileUnderGateAsync(cancellationToken);
        }
        finally
        {
            _refreshGate.Release();
        }

        if (changed)
        {
            ToolsChanged?.Invoke();
        }

        return changed;
    }

    private async Task<bool> ReconcileUnderGateAsync(CancellationToken cancellationToken)
    {
        if (!_autoReconcileEnabled)
        {
            return false;
        }

        var changed = false;
        var stateBefore = ReconcileState();
        try
        {
            changed = await AutoReloadProfileAsync(cancellationToken) == ProfileReload.Reloaded;
            if (!changed)
            {
                bool? promotionHeld = null;
                foreach (var backend in _backends.Values)
                {
                    if (backend.StartRefusal is not null)
                    {
                        // A start that was refused (a promotion was in progress): once the lock is free that text
                        // no longer describes the backend. It is evaluated again, still without starting anything.
                        promotionHeld ??= PromotionLock.IsHeld(PromotionLockPath);
                        if (promotionHeld == false)
                        {
                            await backend.InitializePassivelyAsync(cancellationToken);
                            changed = true;
                            continue;
                        }
                    }

                    changed |= await backend.ReconcileListenerAsync(cancellationToken);
                }
            }

            if (changed)
            {
                RebuildToolMap();
            }

            _lastReconcileFailure = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Said once per distinct failure, not once per round.
            var failure = ex.GetType().Name + ": " + ex.Message;
            if (failure != _lastReconcileFailure)
            {
                _lastReconcileFailure = failure;
                Console.Error.WriteLine($"[hub] reconciliação passiva falhou: {failure}");
            }

            // A tool list notification only when something did change before the failure (the backends were
            // swapped or connected again): then the tool map is rebuilt so that it is never left stale.
            changed = ReconcileState() != stateBefore;
            if (changed)
            {
                try
                {
                    RebuildToolMap();
                }
                catch (ConfigurationException mapError)
                {
                    Console.Error.WriteLine($"[hub] mapa de tools não reconstruído: {mapError.Message}");
                }
            }
        }

        return changed;
    }

    // What a reconciliation can change, in one comparable value: the loaded profile and each backend connection.
    private string ReconcileState() =>
        _profileFingerprint + "|" + string.Join(
            "|",
            _backends.Values.Select(backend =>
                backend.Config.Id + ":" + backend.Online + ":" + backend.Tools.Count + ":" + backend.ConnectedListener));

    private async Task<ProfileReload> AutoReloadProfileAsync(CancellationToken cancellationToken)
    {
        _autoReload = _autoReload with { LastCheckUtc = DateTimeOffset.UtcNow };

        string fingerprint;
        try
        {
            fingerprint = ReadProfileFingerprint();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetAutoReloadOutcome(
                "failed",
                null,
                "profile-unreadable: o JSON do perfil não pôde ser lido agora; nova tentativa na próxima rodada.",
                ex.Message);
            return ProfileReload.Failed;
        }

        if (fingerprint.Equals(_profileFingerprint, StringComparison.Ordinal))
        {
            // Nothing pending any more (loaded by gxhub_refresh, or the edit was undone).
            if (_autoReload.LastOutcome is "deferred" or "failed")
            {
                SetAutoReloadOutcome("none", null, null, null);
            }

            return ProfileReload.Unchanged;
        }

        if (_backends.Values.Any(backend => backend.OwnGatewayRunning))
        {
            // Loading the edit would mean stopping a Gateway this Hub started, maybe in the middle of someone's
            // operation. That stays a deliberate act: gxhub_refresh.
            SetAutoReloadOutcome("deferred", DeferredByOwnGateway, null, null);
            return ProfileReload.Deferred;
        }

        LoadedHubConfig next;
        try
        {
            next = _profileLoader(_loaded.ConfigPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetAutoReloadOutcome(
                "failed",
                null,
                "profile-unreadable: o JSON do perfil não pôde ser lido agora; nova tentativa na próxima rodada.",
                ex.Message);
            return ProfileReload.Failed;
        }
        catch (Exception ex)
        {
            // Whatever the loader throws, a profile that does not load is an invalid profile: the Hub keeps the one
            // it has, says so once, and no tool list changes.
            SetAutoReloadOutcome(
                "failed",
                null,
                "invalid-profile: o JSON do perfil em disco não carrega (inválido, incompleto ou com um caminho " +
                "inexistente); o perfil carregado foi mantido.",
                ex.GetType().Name + ": " + ex.Message);
            return ProfileReload.Failed;
        }

        if (next.Fingerprint.Equals(_profileFingerprint, StringComparison.Ordinal))
        {
            // The file went back to the loaded content between the look at its revision and the load.
            if (_autoReload.LastOutcome is "deferred" or "failed")
            {
                SetAutoReloadOutcome("none", null, null, null);
            }

            return ProfileReload.Unchanged;
        }

        try
        {
            ValidateHotReloadCompatibility(_loaded.Config, next.Config);
        }
        catch (ConfigurationException ex)
        {
            SetAutoReloadOutcome(
                "failed",
                null,
                "incompatible-profile-change: o perfil em disco troca nome, workspace, KB ou política de escrita; " +
                "isso não é recarregado no transporte atual.",
                ex.Message);
            return ProfileReload.Failed;
        }

        if (!ProfileShape.DiffersOnlyInStartup(_loaded.Config, next.Config))
        {
            // Only what a promotion rewrites (backends[].startup) is loaded without anyone asking. Any other change
            // waits for gxhub_refresh.
            SetAutoReloadOutcome("deferred", DeferredToManualRefresh, null, null);
            return ProfileReload.Deferred;
        }

        // The revision recorded is the one of the bytes that were loaded. If the file changed again meanwhile, the
        // next round sees a revision that is not the loaded one and loads it.
        await SwapProfileAsync(next, next.Fingerprint, "auto");
        var tasks = _backends.Values.Select(backend => backend.InitializePassivelyAsync(cancellationToken));
        await Task.WhenAll(tasks);
        SetAutoReloadOutcome("reloaded", null, null, null);
        return ProfileReload.Reloaded;
    }

    // The status carries a code and a sentence without paths or values; the detail goes to stderr only.
    private void SetAutoReloadOutcome(string outcome, string? deferredReason, string? error, string? detail)
    {
        var current = _autoReload;
        if (current.LastOutcome == outcome && current.DeferredReason == deferredReason && current.Error == error)
        {
            return;
        }

        _autoReload = current with
        {
            LastOutcome = outcome,
            LastOutcomeAtUtc = DateTimeOffset.UtcNow,
            DeferredReason = deferredReason,
            Error = error
        };

        switch (outcome)
        {
            case "deferred":
                Console.Error.WriteLine(deferredReason == DeferredToManualRefresh
                    ? "[hub] perfil editado no disco fora de backends[].startup: a mudança só é aplicada por " +
                      "gxhub_refresh."
                    : "[hub] perfil editado no disco, recarga adiada: há Gateway iniciado por este Hub em " +
                      "execução; use gxhub_refresh para recarregar e reiniciá-lo.");
                break;
            case "failed":
                Console.Error.WriteLine($"[hub] recarga automática do perfil não aplicada: {error} {detail}".TrimEnd());
                break;
        }
    }

    public JsonObject StatusJson()
    {
        string? onDiskRevision = null;
        DateTime? profileModifiedUtc = null;
        try
        {
            onDiskRevision = ReadProfileFingerprint()[..12];
            profileModifiedUtc = ProfileEditedUtc(File.GetLastWriteTimeUtc(_loaded.ConfigPath));
        }
        catch (IOException)
        {
            // Arquivo momentaneamente indisponível: o status não falha por isso.
        }
        catch (UnauthorizedAccessException)
        {
        }

        var loadedRevision = _profileFingerprint[..12];
        var backends = new JsonArray();
        foreach (var backend in _backends.Values.OrderBy(item => item.Config.Id, StringComparer.Ordinal))
        {
            backends.Add(backend.StatusJson(loadedRevision, onDiskRevision, profileModifiedUtc));
        }

        var autoReload = _autoReload;
        return new JsonObject
        {
            ["hubVersion"] = HubInfo.Version,
            // How many inheritable handles this Hub took the inheritance from at its start, and how it found them
            // ("nt": the handle table of the process; "scan": every handle value up to a ceiling; "none"): what
            // keeps a Gateway that outlives the Hub from holding the channels of whoever launched the Hub.
            ["inheritableHandlesCleared"] = StandardHandles.LastSweep.Cleared,
            ["inheritableHandlesPath"] = StandardHandles.LastSweep.Path,
            // Which TEMP folder this Hub works in (an identifier, never the path): Hubs with the same one see each
            // other, through the startup lease and the presence; with another one they are strangers.
            ["hubTempRootId"] = HubPresence.TempRootId(),
            // GXMCP_HUB_START_OUTSIDE_JOB as this Hub reads it ("auto", "always" or "never"), and whether a child
            // started the usual way would be in a job: false when the Hub is in none, what the probe said when it is in
            // one, null while the probe has not answered (it runs once, at the first start that needs the answer).
            ["outsideJobPolicy"] = OutsideJobPolicySetting.Name(_outsideJob.Policy),
            ["childWouldBeInJob"] = _outsideJob.ChildWouldBeInJob is bool childInJob ? JsonValue.Create(childInJob) : null,
            // GXMCP_HUB_KEEP_GATEWAY_WHEN_ALONE as this Hub reads it: whether it leaves the Gateway it started running
            // when it exits with no other Hub connected. False under GXMCP_HUB_KEEP_SHARED_GATEWAY=0, which wins.
            ["keepGatewayWhenAlone"] = _keepGatewayWhenAlone && _keepSharedGateway,
            ["profile"] = Config.ProfileName,
            ["profileConfigPath"] = _loaded.ConfigPath,
            ["profileRevision"] = loadedRevision,
            // The revision the file has now, read at status time. When it differs from profileRevision the
            // profile on disk was edited after this Hub loaded it. The Hub loads it by itself unless that would
            // stop a Gateway it started or the edit goes beyond backends[].startup (profileAutoReload says why
            // not); gxhub_refresh always loads it.
            ["profileOnDiskRevision"] = onDiskRevision,
            ["profileReloadPending"] = onDiskRevision is not null &&
                                       !onDiskRevision.Equals(loadedRevision, StringComparison.Ordinal),
            ["lastProfileReloadAtUtc"] = _lastProfileReloadAtUtc?.ToString("O"),
            ["lastProfileReloadTrigger"] = _lastProfileReloadTrigger,
            ["profileAutoReload"] = new JsonObject
            {
                ["enabled"] = _autoReconcileEnabled,
                ["lastCheckUtc"] = autoReload.LastCheckUtc?.ToString("O"),
                ["lastOutcome"] = autoReload.LastOutcome,
                ["lastOutcomeAtUtc"] = autoReload.LastOutcomeAtUtc?.ToString("O"),
                ["deferredReason"] = autoReload.DeferredReason,
                ["error"] = autoReload.Error
            },
            // Whether a promotion of the MCP is in progress right now (the lock file is held by whoever promotes).
            // While it is, this Hub does not start a backend process; connecting to one that listens is unaffected.
            ["promotionLock"] = new JsonObject
            {
                ["held"] = PromotionLock.IsHeld(PromotionLockPath)
            },
            ["knowledgeBasePath"] = Config.KnowledgeBasePath,
            ["writePolicy"] = new JsonObject
            {
                ["mode"] = Config.WritePolicy.Mode,
                ["writerBackend"] = Config.WritePolicy.WriterBackend,
                ["lockScope"] = Config.WritePolicy.Mode == "single-writer"
                    ? "uma instância de Hub por perfil/KB"
                    : "nenhuma trava entre instâncias",
                ["rule"] = Config.WritePolicy.Mode switch
                {
                    "single-writer" => "no máximo uma instância escritora por perfil/KB",
                    "open-writer" => "múltiplas instâncias podem ler e gravar concorrentemente",
                    _ => "somente leitura"
                }
            },
            ["backends"] = backends,
            ["exposedBackendTools"] = CountMappedTools()
        };
    }

    public async Task RefreshAsync(
        bool allowStartup,
        bool notify,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (allowStartup)
            {
                // The Hub startup: a promotion in progress is never waited for. The Hub comes up with the backend
                // offline, and the reconciliation or the next call takes it from there.
                await InitializeAllAllowingStartAsync(StartMode.HubStart, cancellationToken);
            }
            else
            {
                var tasks = _backends.Values.Select(
                    backend => backend.InitializeAsync(allowStartup: false, cancellationToken));
                await Task.WhenAll(tasks);
            }

            RebuildToolMap();
        }
        finally
        {
            _refreshGate.Release();
        }

        if (notify)
        {
            ToolsChanged?.Invoke();
        }
    }

    // Returns null, or the refusal (a code and a sentence without paths) when a backend had to be started and a
    // promotion was still in progress after the wait.
    public async Task<string?> RefreshFromProfileAsync(
        bool allowStartup,
        bool notify,
        CancellationToken cancellationToken)
    {
        string? refusal = null;
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            await ReloadProfileManuallyIfChangedAsync();

            // The profile on disk is the loaded one now: nothing is deferred or failed any more.
            if (_autoReload.LastOutcome is "deferred" or "failed")
            {
                SetAutoReloadOutcome("none", null, null, null);
            }

            if (allowStartup)
            {
                try
                {
                    refusal = await InitializeAllAllowingStartAsync(StartMode.Refresh, cancellationToken);
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or ConfigurationException))
                {
                    // The backend process could not be started. As in a tool call, the caller gets the code and the
                    // sentence the backend recorded; the exception text, with the command path and the working
                    // directory, stays in stderr. A profile that does not load remains an error of the call.
                    Console.Error.WriteLine(
                        $"[hub] gxhub_refresh não iniciou o backend: {ex.GetType().Name}: {ex.Message}");
                    refusal = _backends.Values
                                  .Select(backend => backend.Error)
                                  .FirstOrDefault(error =>
                                      error is not null &&
                                      error.StartsWith("backend-start-", StringComparison.Ordinal))
                              ?? (ex is TimeoutException
                                  ? BackendRuntime.StartTimeoutText
                                  : BackendRuntime.StartFailedText);
                }
            }
            else
            {
                var tasks = _backends.Values.Select(
                    backend => backend.InitializeAsync(allowStartup: false, cancellationToken));
                await Task.WhenAll(tasks);
            }

            RebuildToolMap();
        }
        finally
        {
            _refreshGate.Release();
        }

        if (notify)
        {
            ToolsChanged?.Invoke();
        }

        return refusal;
    }

    // The deliberate reload of gxhub_refresh: a Gateway this Hub started is stopped here and started again by the
    // caller. An invalid or incompatible profile is an error of the call, as before.
    private async Task<bool> ReloadProfileManuallyIfChangedAsync()
    {
        var fingerprint = ReadProfileFingerprint();
        if (fingerprint.Equals(_profileFingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        var next = _profileLoader(_loaded.ConfigPath);
        if (next.Fingerprint.Equals(_profileFingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        ValidateHotReloadCompatibility(_loaded.Config, next.Config);
        await SwapProfileAsync(next, next.Fingerprint, "manual");
        return true;
    }

    // Every initialization that may start a backend process goes through here: the Hub startup, gxhub_refresh and
    // a tool call that needs an offline backend.
    private async Task<string?> InitializeAllAllowingStartAsync(StartMode mode, CancellationToken cancellationToken)
    {
        string? firstRefusal = null;
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var pass = 0; pass < 20; pass++)
        {
            var backendId = _backends.Keys.FirstOrDefault(id => !done.Contains(id));
            if (backendId is null)
            {
                break;
            }

            done.Add(backendId);
            var fingerprintBefore = _profileFingerprint;
            var refusal = await InitializeAllowingStartAsync(backendId, mode, cancellationToken);
            firstRefusal ??= refusal;
            if (!fingerprintBefore.Equals(_profileFingerprint, StringComparison.Ordinal))
            {
                // The profile was reloaded on the way: every backend is a new object and goes through again.
                done.Clear();
                done.Add(backendId);
            }
        }

        return firstRefusal;
    }

    // Starting a backend process is decided at one point, inside the backend initialization and only when nobody
    // listens on its endpoint: StartGuard refuses while a promotion holds the lock or while the profile on disk is
    // not the loaded one. This method reacts to a refusal outside the startup lease: it waits for the promotion
    // (never at the Hub startup), reloads the profile, and asks again, so that a backend is never started from a
    // profile the promotion has just rewritten. A Gateway that already listens is connected without any of this.
    private async Task<string?> InitializeAllowingStartAsync(
        string backendId,
        StartMode mode,
        CancellationToken cancellationToken)
    {
        var deadlineUtc = DateTime.UtcNow + PromotionLockWait;
        string? refusal = null;
        for (var round = 0; round < MaxStartRounds; round++)
        {
            if (!_backends.TryGetValue(backendId, out var backend))
            {
                return null;
            }

            await backend.InitializeAsync(
                allowStartup: true,
                cancellationToken,
                boundedHandshake: mode == StartMode.Call,
                startGuard: StartGuard);
            refusal = backend.StartRefusal;
            if (refusal is null)
            {
                // The Gateway that was listening left between the handshake and the presence of this Hub: the Hub
                // that had started it was on its way out and did not see this one. Asked again, nobody listens
                // and the backend is started here.
                if (backend.GatewayLeftWhileConnecting && !backend.Online)
                {
                    continue;
                }

                return null;
            }

            if (refusal.StartsWith(PromotionLock.Code, StringComparison.Ordinal))
            {
                if (mode == StartMode.HubStart || await WaitForPromotionLockAsync(deadlineUtc, cancellationToken))
                {
                    return refusal;
                }

                continue;
            }

            // The profile on disk is not the loaded one.
            if (mode == StartMode.Refresh)
            {
                await ReloadProfileManuallyIfChangedAsync();
                continue;
            }

            switch (await AutoReloadProfileAsync(cancellationToken))
            {
                case ProfileReload.Deferred:
                    return _autoReload.DeferredReason == DeferredToManualRefresh
                        ? ProfileReloadDeferredCode +
                          ": o perfil mudou no disco em campos que a recarga automática não aplica; " +
                          "use gxhub_refresh para carregá-lo antes de iniciar o backend."
                        : ProfileReloadDeferredCode +
                          ": o perfil mudou no disco e há Gateway iniciado por este Hub em execução; " +
                          "use gxhub_refresh para recarregar o perfil e reiniciá-lo.";
                case ProfileReload.Failed:
                    return _autoReload.Error ?? refusal;
            }
        }

        return refusal;
    }

    // Asked by a backend at the only point where a process is about to be started. It never waits.
    private string? StartGuard()
    {
        Interlocked.Increment(ref _promotionLockChecks);
        if (PromotionLock.IsHeld(PromotionLockPath))
        {
            return PromotionLock.Refusal;
        }

        return ProfileChangedOnDisk()
            ? ProfileChangedCode + ": o perfil mudou no disco e o backend não é iniciado pelo perfil antigo; " +
              "profileAutoReload diz se a mudança foi carregada."
            : null;
    }

    private bool ProfileChangedOnDisk()
    {
        try
        {
            return !ReadProfileFingerprint().Equals(_profileFingerprint, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It cannot be confirmed as the loaded one: treated as changed, and the reload reports why.
            return true;
        }
    }

    // Returns true when the promotion still holds the lock at the deadline.
    private async Task<bool> WaitForPromotionLockAsync(DateTime deadlineUtc, CancellationToken cancellationToken)
    {
        var announced = false;
        while (true)
        {
            Interlocked.Increment(ref _promotionLockChecks);
            if (!PromotionLock.IsHeld(PromotionLockPath))
            {
                return false;
            }

            if (DateTime.UtcNow >= deadlineUtc)
            {
                return true;
            }

            if (!announced)
            {
                announced = true;
                Console.Error.WriteLine(
                    "[hub] promoção do MCP em andamento: aguardando a trava para iniciar o backend.");
            }

            await Task.Delay(PromotionLockPoll, cancellationToken);
        }
    }


    private async Task SwapProfileAsync(LoadedHubConfig next, string fingerprint, string trigger)
    {
        var previousBackends = _backends;
        _backends = CreateBackends(next.Config, fingerprint);
        // The presence of this Hub stays on every endpoint the new profile still has, across the swap of the
        // backend objects, and is let go on the ones it no longer has.
        _presence.RetainOnly(_backends.Values
            .Select(backend => backend.Endpoint)
            .Where(endpoint => endpoint is not null)
            .Select(endpoint => endpoint!));
        _loaded = next;
        _profileFingerprint = fingerprint;
        _lastProfileReloadAtUtc = DateTimeOffset.UtcNow;
        _lastProfileReloadTrigger = trigger;
        lock (_mapGate)
        {
            _toolMap = new Dictionary<string, BackendTool>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var backend in previousBackends.Values)
        {
            await backend.DisposeAsync();
        }

        Console.Error.WriteLine(
            $"[hub] perfil recarregado ({trigger}) sem encerrar o transporte STDIO: {_loaded.ConfigPath}");
    }

    internal static void ValidateHotReloadCompatibility(HubConfig current, HubConfig next)
    {
        if (!current.ProfileName.Equals(next.ProfileName, StringComparison.Ordinal) ||
            !Path.GetFullPath(current.WorkspaceRoot).Equals(
                Path.GetFullPath(next.WorkspaceRoot), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(current.KnowledgeBasePath).Equals(
                Path.GetFullPath(next.KnowledgeBasePath), StringComparison.OrdinalIgnoreCase) ||
            !current.WritePolicy.Mode.Equals(next.WritePolicy.Mode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                current.WritePolicy.WriterBackend,
                next.WritePolicy.WriterBackend,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfigurationException(
                "gxhub_refresh não pode trocar perfil, workspace, KB ou política de escrita " +
                "no transporte atual. Restaure esses campos ou reinicie a tarefa.");
        }
    }

    private Dictionary<string, BackendRuntime> CreateBackends(HubConfig config, string profileFingerprint)
    {
        var backends = config.Backends.ToDictionary(
            item => item.Id,
            item => _backendFactory(config, item),
            StringComparer.OrdinalIgnoreCase);
        foreach (var backend in backends.Values)
        {
            backend.ApplyProfileRevision(profileFingerprint[..12]);
            backend.UsePresence(_presence);
            backend.UseOutsideJob(_outsideJob);
            backend.PresenceLeaseWait = _presenceLeaseWait;
            backend.PresenceRetryInterval = _presenceRetryInterval;
        }

        return backends;
    }

    // Every look at the profile on disk goes through here, so that the Hub knows when the content changed even if
    // the file date does not say so: a profile restored by copy keeps the date it had in the backup.
    private string ReadProfileFingerprint()
    {
        var fingerprint = HubConfigLoader.ReadProfileFingerprint(_loaded.ConfigPath);
        lock (_profileWatchGate)
        {
            if (!fingerprint.Equals(_observedOnDiskFingerprint, StringComparison.Ordinal))
            {
                // The content changed between the previous look and this one: the edit is not older than the
                // previous look, whatever the file date is.
                _profileEditNotBeforeUtc = _observedOnDiskAtUtc;
                _observedOnDiskFingerprint = fingerprint;
            }

            _observedOnDiskAtUtc = DateTime.UtcNow;
        }

        return fingerprint;
    }

    // When the profile on disk got its current content, as far as this Hub can tell: the file date, or the last
    // time this Hub still saw the previous content there, whichever is later.
    private DateTime ProfileEditedUtc(DateTime fileModifiedUtc)
    {
        lock (_profileWatchGate)
        {
            return _profileEditNotBeforeUtc is DateTime notBefore && notBefore > fileModifiedUtc
                ? notBefore
                : fileModifiedUtc;
        }
    }

    private void RebuildToolMap()
    {
        var next = new Dictionary<string, BackendTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var backend in _backends.Values.Where(item => item.Online))
        {
            foreach (var tool in backend.Tools)
            {
                var allowed = IsAllowed(backend, tool);
                if (!allowed && Config.WritePolicy.HideBlockedTools)
                {
                    continue;
                }

                if (!next.TryAdd(tool.ExposedName, tool))
                {
                    throw new ConfigurationException($"Nome de tool exposta duplicado: {tool.ExposedName}");
                }
            }
        }

        lock (_mapGate)
        {
            _toolMap = next;
        }
    }

    private bool IsAllowed(BackendRuntime backend, BackendTool tool)
    {
        if (tool.IsReadOnly(backend.Config))
        {
            return true;
        }

        return Config.WritePolicy.Mode is "single-writer" or "open-writer" &&
               string.Equals(
                   Config.WritePolicy.WriterBackend,
                   backend.Config.Id,
                   StringComparison.OrdinalIgnoreCase);
    }

    private async Task<JsonNode?> GenericCallAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var backendId = arguments["backend"]?.GetValue<string>();
            var toolName = arguments["tool"]?.GetValue<string>();
            var toolArguments = arguments["arguments"] as JsonObject ?? new JsonObject();
            if (string.IsNullOrWhiteSpace(backendId) || string.IsNullOrWhiteSpace(toolName))
            {
                return ToolText("gxhub_call exige backend e tool.", isError: true);
            }

            var changed = await ReconcileUnderGateAsync(cancellationToken);
            if (await EnsurePresenceAsync(cancellationToken))
            {
                changed = true;
            }

            if (_backends.TryGetValue(backendId, out var offline) &&
                _autoReconcileEnabled && offline.Config.Enabled && !offline.Online)
            {
                var startError = await StartOfflineBackendForCallAsync(offline, cancellationToken);
                changed = true;
                if (startError is not null)
                {
                    ToolsChanged?.Invoke();
                    return ToolText(startError, isError: true);
                }
            }

            if (changed)
            {
                ToolsChanged?.Invoke();
            }

            if (!_backends.TryGetValue(backendId, out var backend) || !backend.Online)
            {
                return ToolText($"Backend '{backendId}' não existe ou está offline.", isError: true);
            }

            var tool = backend.Tools.FirstOrDefault(
                item => item.OriginalName.Equals(toolName, StringComparison.OrdinalIgnoreCase));
            if (tool is null)
            {
                return ToolText($"Tool '{toolName}' não encontrada no backend '{backendId}'.", isError: true);
            }
            if (!IsAllowed(backend, tool))
            {
                return PolicyBlocked(tool);
            }

            try
            {
                return await backend.CallAsync(tool.OriginalName, toolArguments, cancellationToken)
                       ?? ToolText("Backend devolveu result vazio.", isError: true);
            }
            catch (Exception ex)
            {
                return ToolText($"Falha no backend '{backendId}': {ex.Message}", isError: true);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private JsonObject ListToolsJson(JsonObject arguments)
    {
        var backendFilter = arguments["backend"]?.GetValue<string>();
        var includeBlocked = arguments["includeBlocked"]?.GetValue<bool>() == true;
        var backends = new JsonArray();

        foreach (var backend in _backends.Values
                     .Where(item => string.IsNullOrWhiteSpace(backendFilter) ||
                                    item.Config.Id.Equals(backendFilter, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(item => item.Config.Id, StringComparer.Ordinal))
        {
            var tools = new JsonArray();
            foreach (var tool in backend.Tools.OrderBy(item => item.OriginalName, StringComparer.Ordinal))
            {
                var allowed = IsAllowed(backend, tool);
                if (!allowed && !includeBlocked)
                {
                    continue;
                }
                tools.Add(new JsonObject
                {
                    ["name"] = tool.OriginalName,
                    ["exposedName"] = tool.ExposedName,
                    ["readOnly"] = tool.IsReadOnly(backend.Config),
                    ["allowed"] = allowed
                });
            }

            backends.Add(new JsonObject
            {
                ["id"] = backend.Config.Id,
                ["online"] = backend.Online,
                ["tools"] = tools
            });
        }

        return new JsonObject { ["backends"] = backends };
    }

    private JsonObject PolicyBlocked(BackendTool tool)
    {
        return ToolText(
            $"[POLÍTICA] '{tool.ExposedName}' foi bloqueada. O perfil '{Config.ProfileName}' está " +
            $"em modo '{Config.WritePolicy.Mode}' com escritor " +
            $"'{Config.WritePolicy.WriterBackend ?? "nenhum"}'.",
            isError: true);
    }

    private int CountMappedTools()
    {
        lock (_mapGate)
        {
            return _toolMap.Count;
        }
    }

    private static IEnumerable<JsonObject> MetaToolDefinitions()
    {
        yield return ToolDefinition(
            "gxhub_status",
            "Mostra perfil, política de escrita e estado dos backends, incluindo se o Gateway em execução " +
            "usa o perfil como está agora (backends[].gateway.profileApplied) e quantos outros Hubs estão " +
            "conectados a ele (backends[].gateway.otherHubsConnected).",
            new JsonObject());
        yield return ToolDefinition(
            "gxhub_list_tools",
            "Lista as tools descobertas por backend e se a política permite chamá-las.",
            new JsonObject
            {
                ["backend"] = StringProperty("Filtra pelo id do backend."),
                ["includeBlocked"] = BooleanProperty("Inclui tools bloqueadas pela política.")
            });
        yield return ToolDefinition(
            "gxhub_refresh",
            "Recarrega o perfil em disco e redescobre/reinicia backends sem encerrar o transporte STDIO. " +
            "O Hub já recarrega o perfil sozinho depois de uma promoção; use o refresh quando " +
            "gxhub_status mostrar a recarga adiada (profileAutoReload.lastOutcome=deferred), porque só ele " +
            "reinicia um Gateway que este Hub iniciou e só ele aplica mudança do perfil fora de " +
            "backends[].startup, e após abrir/fechar uma KB no IDE. Quando o perfil mudou no disco, o refresh " +
            "carrega o perfil novo e reinicia o Gateway que este Hub iniciou, mesmo com outros Hubs conectados " +
            "a ele (confira antes backends[].gateway.otherHubsConnected); sem mudança no perfil ele só reconecta.",
            new JsonObject());
        yield return ToolDefinition(
            "gxhub_call",
            "Chama uma tool por nome original. Útil após gxhub_refresh; a mesma política de escrita continua obrigatória.",
            new JsonObject
            {
                ["backend"] = StringProperty("Id: native, brain ou objgen."),
                ["tool"] = StringProperty("Nome original da tool no backend."),
                ["arguments"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Argumentos originais da tool."
                }
            },
            new JsonArray("backend", "tool"),
            readOnly: false,
            destructive: true,
            idempotent: false);
    }

    private static JsonObject ToolDefinition(
        string name,
        string description,
        JsonObject properties,
        JsonArray? required = null,
        bool readOnly = true,
        bool destructive = false,
        bool idempotent = true)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };
        if (required is not null)
        {
            schema["required"] = required;
        }

        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
            ["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = readOnly,
                ["destructiveHint"] = destructive,
                ["idempotentHint"] = idempotent,
                ["openWorldHint"] = false
            }
        };
    }

    private static JsonObject StringProperty(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description
    };

    private static JsonObject BooleanProperty(string description) => new()
    {
        ["type"] = "boolean",
        ["description"] = description
    };

    internal static JsonObject ToolText(string text, bool isError = false)
    {
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text
                }
            },
            ["isError"] = isError
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_reconcileCts is not null)
        {
            _reconcileCts.Cancel();
            if (_reconcileLoop is not null)
            {
                try
                {
                    await _reconcileLoop;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[hub] reconciliação periódica encerrou com erro: {ex.Message}");
                }
            }

            _reconcileCts.Dispose();
        }

        // The Hub is leaving: a Gateway it started is only stopped when no other Hub is connected to it.
        foreach (var backend in _backends.Values)
        {
            var decision = await backend.ShutdownAsync(_keepSharedGateway, ExitLeaseWait, _keepGatewayWhenAlone);
            HubExitLog.Append(Config, Environment.ProcessId, backend.Config.Id, decision);
        }

        _presence.Dispose();
        _writerLease?.Dispose();
        _refreshGate.Dispose();
    }

    private enum StartMode
    {
        HubStart,
        Call,
        Refresh
    }

    private enum ProfileReload
    {
        Unchanged,
        Reloaded,
        Deferred,
        Failed
    }

    private sealed record AutoReloadState(
        DateTimeOffset? LastCheckUtc,
        string LastOutcome,
        DateTimeOffset? LastOutcomeAtUtc,
        string? DeferredReason,
        string? Error)
    {
        public static readonly AutoReloadState Initial = new(null, "none", null, null, null);
    }
}
