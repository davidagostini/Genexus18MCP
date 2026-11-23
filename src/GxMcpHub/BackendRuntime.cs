using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace GxMcpHub;

internal sealed class BackendRuntime : IAsyncDisposable
{
    // A process that listens but does not answer MCP is tried a few times in a row and then left alone until the
    // listener changes or a tool call asks for the backend. Only consecutive failures count: a connection that works
    // starts the count again.
    private const int MaxPassiveFailuresPerListener = 3;

    // How many times a handshake is repeated when the process behind the endpoint changes while it runs.
    private const int MaxHandshakeAttempts = 3;

    // What the status and a caller get when the backend process could not be started: a code and a sentence, never
    // the exception text, which carries the command path and the working directory. The detail goes to stderr.
    internal const string StartTimeoutCode = "backend-start-timeout";
    internal const string StartFailedCode = "backend-start-failed";
    internal const string StartBusyCode = "backend-start-busy";

    internal const string StartTimeoutText =
        StartTimeoutCode + ": o processo do backend foi iniciado e não abriu o endpoint no prazo; ele foi encerrado.";

    internal const string StartFailedText =
        StartFailedCode + ": o processo do backend não pôde ser iniciado; o detalhe está no stderr do Hub.";

    internal const string StartBusyText =
        StartBusyCode + ": outra instância do Hub está iniciando este backend e não terminou no prazo.";

    private readonly HubConfig _hubConfig;
    private readonly ProcessSupervisor _supervisor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<Uri, HttpMessageHandler?>? _messageHandlerFactory;
    private readonly Func<Uri, ListenerLookup> _listenerLookup;
    private readonly Func<int, int, bool?> _isSameOrDescendant;
    private BackendClient? _client;
    private ListenerLookup? _passiveFailureListener;
    private int _passiveFailures;
    private HubPresence? _presence;
    private bool _ownsPresence;
    private bool _presenceLeaseWarned;
    private DateTime _presenceRetryAfterUtc;

    public BackendRuntime(
        HubConfig hubConfig,
        BackendConfig config,
        Func<Uri, HttpMessageHandler?>? messageHandlerFactory = null,
        Func<Uri, ListenerLookup>? listenerLookup = null,
        Func<int, int, bool?>? processTree = null)
    {
        _hubConfig = hubConfig;
        Config = config;
        _supervisor = new ProcessSupervisor(config);
        _messageHandlerFactory = messageHandlerFactory;
        _listenerLookup = listenerLookup ?? ListenerInspector.Find;
        _isSameOrDescendant = processTree ?? ProcessTree.IsSameOrDescendant;
    }

    public BackendConfig Config { get; }
    public bool Online { get; private set; }
    public string? Error { get; private set; }
    public Uri? ConnectedEndpoint { get; private set; }
    public JsonObject? ServerInfo { get; private set; }
    public IReadOnlyList<BackendTool> Tools { get; private set; } = Array.Empty<BackendTool>();

    // True while a process this Hub started is alive and can be the one behind the endpoint: it listens (itself, or
    // through a process it started, as with a wrapper script), or nobody listens yet. A live process of this Hub is
    // not enough: one that is alive while another process owns the port lost the port and serves nobody.
    public bool OwnGatewayRunning
    {
        get
        {
            if (!_supervisor.StartedByHub)
            {
                return false;
            }

            var endpoint = ResolveCandidates().FirstOrDefault();
            return endpoint is null || !HasLostProcess(_listenerLookup(endpoint));
        }
    }

    // The process that was listening on the endpoint when the current connection was made.
    public ListenerLookup? ConnectedListener { get; private set; }

    // True when the last initialization made the handshake and then found that nobody listened any more: the Hub
    // that had started the Gateway stopped it on its way out, before it could see this Hub. Whoever may start the
    // backend asks again.
    public bool GatewayLeftWhileConnecting { get; private set; }

    // How long a connection waits for the startup lease of the endpoint to register the presence of this Hub.
    internal TimeSpan PresenceLeaseWait { get; set; } = TimeSpan.FromSeconds(2);

    // How long after a failed registration the presence is not tried again by a call or a round (the connection
    // itself always tries): a Hub that cannot register, because the lease stays busy or the folder cannot be
    // written, does not pay for it on every call.
    internal TimeSpan PresenceRetryInterval { get; set; } = TimeSpan.FromSeconds(10);

    // How long the Hub that stops its Gateway on the way out keeps the startup lease while something still listens.
    internal TimeSpan ListenerGoneWait { get; set; } = TimeSpan.FromSeconds(2);

    internal Uri? Endpoint => ResolveCandidates().FirstOrDefault();

    // The presence of the Hub this backend belongs to. A backend used on its own (the self-tests) keeps one of its own.
    private HubPresence Presence
    {
        get
        {
            if (_presence is null)
            {
                _presence = new HubPresence();
                _ownsPresence = true;
            }

            return _presence;
        }
    }

    // Called by the Hub right after creating the backend: every backend of a Hub shares the decision to leave the job
    // of the client, and the one probe that informs it.
    internal void UseOutsideJob(OutsideJobStarter starter) => _supervisor.OutsideJob = starter;

    // Called by the Hub right after creating the backend: every backend of a Hub shares the presence of that Hub, so
    // that a profile reload, which replaces the backend objects, does not let go of an endpoint it still uses.
    internal void UsePresence(HubPresence shared)
    {
        if (_ownsPresence)
        {
            _presence?.Dispose();
        }

        _presence = shared;
        _ownsPresence = false;
    }

    // Why the last initialization did not start the backend process although nobody was listening: the text the
    // start guard returned (a code and a sentence). Null when the guard let it start or was never reached.
    public string? StartRefusal { get; private set; }

    // The initialization the Hub does on its own (periodic round, before a call): it never starts a process, and a
    // process that accepts the connection without answering MCP cannot hold the Hub for the whole call timeout.
    public Task InitializePassivelyAsync(CancellationToken cancellationToken) =>
        InitializeAsync(allowStartup: false, cancellationToken, boundedHandshake: true);

    // How long an automatic handshake (initialize + tools/list) may take before the endpoint counts as not answering.
    internal TimeSpan HandshakeBudget => TimeSpan.FromSeconds((_hubConfig.ConnectTimeoutSeconds * 2) + 3);

    // startGuard is asked at one point only: nobody listens on the endpoint and the process is about to be started.
    // A non-null answer keeps the backend offline with that text and starts nothing. A Gateway that already listens
    // is connected without the guard ever being asked.
    public async Task InitializeAsync(
        bool allowStartup,
        CancellationToken cancellationToken,
        bool boundedHandshake = false,
        Func<string?>? startGuard = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Online = false;
            Error = null;
            StartRefusal = null;
            GatewayLeftWhileConnecting = false;
            Tools = Array.Empty<BackendTool>();
            ConnectedEndpoint = null;
            ConnectedListener = null;
            ServerInfo = null;
            _client?.Dispose();
            _client = null;

            if (!Config.Enabled)
            {
                Error = "desabilitado no perfil";
                return;
            }

            var candidates = ResolveCandidates().ToArray();
            if (candidates.Length == 0)
            {
                Error = "nenhum endpoint pôde ser resolvido";
                return;
            }

            // A process of this Hub that is alive while another process owns the port is not the Gateway of this
            // backend: it is stopped, and the backend is treated as started by another launcher.
            StopLostProcess(candidates[0]);

            if (allowStartup && Config.Startup is not null)
            {
                var startupTimeout = TimeSpan.FromSeconds(
                    Config.Startup.StartupTimeoutSeconds + _hubConfig.ConnectTimeoutSeconds + 5);
                BackendStartupLease startupLease;
                try
                {
                    startupLease = await BackendStartupLease.AcquireAsync(
                        candidates,
                        startupTimeout,
                        cancellationToken);
                }
                catch (TimeoutException ex)
                {
                    Console.Error.WriteLine($"[{Config.Id}] início do backend não coordenado: {ex.Message}");
                    Error = StartBusyText;
                    throw;
                }

                using (startupLease)
                {
                    // Another Hub process may have started the shared loopback backend while
                    // this instance was waiting for the startup lease. Recheck under the lock
                    // so only one process attempts to bind the configured port.
                    if (!await AnyEndpointListeningAsync(candidates, cancellationToken))
                    {
                        // A process of this Hub that outlived its own startup deadline without opening the endpoint
                        // will not open it. It is stopped here, so that this attempt starts a new process instead of
                        // waiting on that one again. The connection attempt above can fail for a moment under load:
                        // the process is only stopped when the system listener table also says that nobody listens
                        // on the endpoint.
                        if (_supervisor.StartedUtc is DateTime startedUtc &&
                            DateTime.UtcNow - startedUtc > TimeSpan.FromSeconds(Config.Startup.StartupTimeoutSeconds) &&
                            _listenerLookup(candidates[0]).State == ListenerLookupState.NotFound)
                        {
                            _supervisor.Stop("não abriu o endpoint dentro do prazo de início");
                        }

                        var refusal = startGuard?.Invoke();
                        if (refusal is not null)
                        {
                            StartRefusal = refusal;
                            Error = refusal;
                            return;
                        }

                        try
                        {
                            await _supervisor.StartAsync(cancellationToken);
                            await WaitForEndpointAsync(candidates, cancellationToken);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            var timedOut = ex is TimeoutException;
                            Console.Error.WriteLine(
                                $"[{Config.Id}] início do backend falhou: {ex.GetType().Name}: {ex.Message}");
                            if (timedOut)
                            {
                                _supervisor.Stop("não abriu o endpoint dentro do prazo de início");
                            }

                            Error = timedOut ? StartTimeoutText : StartFailedText;
                            throw;
                        }

                        // Another process may have taken the port while this one was starting.
                        StopLostProcess(candidates[0]);
                    }
                }
            }

            // gxhub_refresh and the Hub startup keep the handshake unbounded, as before; the automatic paths bound it.
            using var handshakeBudget = boundedHandshake
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            handshakeBudget?.CancelAfter(HandshakeBudget);
            var handshakeToken = handshakeBudget?.Token ?? cancellationToken;

            var failures = new List<string>();
            foreach (var endpoint in candidates)
            {
                BackendClient? client = null;
                try
                {
                    ValidateLoopback(endpoint);
                    BackendTool[] tools;
                    ListenerLookup listener;
                    for (var attempt = 1; ; attempt++)
                    {
                        // The process behind the endpoint is looked up before and after the handshake. If it changed
                        // in between, what was learned may belong to either process: the handshake is made again.
                        var listenerBefore = _listenerLookup(endpoint);
                        client = new BackendClient(
                            Config,
                            endpoint,
                            _hubConfig.ConnectTimeoutSeconds,
                            _hubConfig.CallTimeoutSeconds,
                            _messageHandlerFactory?.Invoke(endpoint));
                        await client.InitializeAsync(handshakeToken);
                        var definitions = await client.ListToolsAsync(handshakeToken);
                        tools = definitions.Select(ToBackendTool).ToArray();
                        listener = _listenerLookup(endpoint);
                        if (listener.Equals(listenerBefore) &&
                            await EnterPresenceAsync(candidates, endpoint, listener, cancellationToken))
                        {
                            break;
                        }

                        client.Dispose();
                        client = null;
                        if (GatewayLeftWhileConnecting)
                        {
                            throw new BackendRpcException(
                                -32000,
                                "o Gateway deixou de escutar durante a conexão");
                        }

                        if (attempt >= MaxHandshakeAttempts)
                        {
                            throw new BackendRpcException(
                                -32000,
                                "o processo que escuta o endpoint mudou durante a conexão");
                        }
                    }

                    // Nothing below fails: the client is only kept once the whole handshake is done.
                    _client = client;
                    ConnectedEndpoint = endpoint;
                    ConnectedListener = listener;
                    ServerInfo = client.ServerInfo;
                    Tools = tools;
                    Online = true;
                    Error = null;
                    Console.Error.WriteLine(
                        $"[{Config.Id}] conectado em {endpoint}; {Tools.Count} tools descobertas.");
                    return;
                }
                catch (Exception ex)
                {
                    if (!ReferenceEquals(client, _client))
                    {
                        client?.Dispose();
                    }
                    var outOfBudget = handshakeBudget is { IsCancellationRequested: true } &&
                                      !cancellationToken.IsCancellationRequested;
                    failures.Add(endpoint + ": " + (outOfBudget
                        ? $"sem resposta MCP em {HandshakeBudget.TotalSeconds:0} s"
                        : ex.Message));
                }
            }

            Error = failures.Count == 0 ? "sem endpoint disponível" : string.Join(" | ", failures);
        }
        finally
        {
            // The presence follows the connection: a backend that ends up offline holds none. One that connects
            // again to the same endpoint keeps the one it had all along.
            if (!Online)
            {
                LeavePresence();
            }

            _gate.Release();
        }
    }

    // Registers the presence of this Hub on the endpoint it has just made the handshake with. Returns false when the
    // process behind the endpoint is no longer the one of the handshake, and the connection must not be kept.
    //
    // The presence is taken under the startup lease of the endpoint. The Hub that started a Gateway takes the same
    // lease on its way out, lets go of its own presence, and stops the Gateway only when no other presence is held.
    // So either that Hub decided before this point, and the look at the listener below finds the Gateway gone, or
    // it decides after it and sees this Hub.
    //
    // The listener is looked at whether or not the presence could be registered. A Hub that waited for the lease
    // longer than the time it was given is not seen by the one that is stopping the Gateway, and must not stay
    // connected to a Gateway that is gone or going: that Hub holds the lease while it stops the Gateway, which can
    // take longer than the wait.
    private async Task<bool> EnterPresenceAsync(
        Uri[] candidates,
        Uri endpoint,
        ListenerLookup connected,
        CancellationToken cancellationToken)
    {
        if (Presence.IsHeld(endpoint))
        {
            return true;
        }

        using var lease = await BackendStartupLease.TryAcquireAsync(candidates, PresenceLeaseWait, cancellationToken);
        var registered = false;
        if (lease is null)
        {
            // Connected without being seen by the other Hubs, as before the presence existed. A call or a round tries
            // again.
            if (!_presenceLeaseWarned)
            {
                _presenceLeaseWarned = true;
                Console.Error.WriteLine(
                    $"[{Config.Id}] presença deste Hub ainda não registrada: a trava de início do endpoint está ocupada.");
            }
        }
        else
        {
            _presenceLeaseWarned = false;
            registered = Presence.TryEnter(endpoint);
        }

        if (!registered)
        {
            _presenceRetryAfterUtc = DateTime.UtcNow + PresenceRetryInterval;
        }

        if (connected.State != ListenerLookupState.Found)
        {
            return true;
        }

        var now = _listenerLookup(endpoint);
        if (now.State == ListenerLookupState.NotInspectable || now.Equals(connected))
        {
            return true;
        }

        if (now.State == ListenerLookupState.NotFound)
        {
            GatewayLeftWhileConnecting = true;
            if (registered)
            {
                Presence.Leave(endpoint);
            }
        }

        // Another process listens now: the handshake is made again with it.
        return false;
    }

    private void LeavePresence()
    {
        if (_presence is not null && Endpoint is Uri endpoint)
        {
            _presence.Leave(endpoint);
        }
    }

    public async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!Online || _client is null)
            {
                return false;
            }

            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _hubConfig.ConnectTimeoutSeconds)));
            try
            {
                // An MCP tools/list round-trip proves more than a stale process flag or an
                // open TCP socket: the configured endpoint is accepting this Hub's session
                // and can answer protocol traffic now.
                var definitions = await _client.ListToolsAsync(probeCts.Token);
                Tools = definitions.Select(ToBackendTool).ToArray();
                ServerInfo = _client.ServerInfo;
                Error = null;
                Online = true;
                return true;
            }
            catch (Exception ex)
            {
                Online = false;
                Tools = Array.Empty<BackendTool>();
                Error = "falha na verificação ativa MCP: " + ex.Message;
                LeavePresence();
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<JsonNode?> CallAsync(
        string originalToolName,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var client = _client ?? throw new BackendRpcException(-32000, $"Backend '{Config.Id}' offline.");
        return await client.CallToolAsync(originalToolName, arguments, cancellationToken);
    }

    public void ApplyProfileRevision(string revision) => _supervisor.ProfileRevision = revision;

    // Passive: never starts a process and never stops one that listens. The connection is made again when the process
    // that listens on the endpoint is not the one this runtime connected to (a promotion replaced the Gateway, or it
    // exited), and an offline backend is connected when a listener appears. A process of this Hub that lost the port
    // to another process is stopped on the way. Returns true when the backend state was evaluated again, so the
    // caller rebuilds the tool map.
    public async Task<bool> ReconcileListenerAsync(CancellationToken cancellationToken)
    {
        if (!Config.Enabled)
        {
            return false;
        }

        var endpoint = ResolveCandidates().FirstOrDefault();
        if (endpoint is null)
        {
            return false;
        }

        // Connected while the startup lease of the endpoint was busy: the presence is tried again here. If the
        // Gateway is gone by now, the backend goes offline and the caller rebuilds the tool map.
        if (await EnsurePresenceAsync(cancellationToken))
        {
            return true;
        }

        var current = _listenerLookup(endpoint);
        if (!HasLostProcess(current))
        {
            if (Online)
            {
                var connected = ConnectedListener;
                if (connected is null ||
                    connected.State != ListenerLookupState.Found ||
                    current.State == ListenerLookupState.NotInspectable ||
                    current.Equals(connected))
                {
                    return false;
                }
            }
            else
            {
                if (current.State != ListenerLookupState.Found)
                {
                    return false;
                }

                if (current.Equals(_passiveFailureListener) && _passiveFailures >= MaxPassiveFailuresPerListener)
                {
                    return false;
                }
            }
        }

        await InitializePassivelyAsync(cancellationToken);
        if (Online)
        {
            _passiveFailureListener = null;
            _passiveFailures = 0;
        }
        else if (current.Equals(_passiveFailureListener))
        {
            _passiveFailures++;
        }
        else
        {
            _passiveFailureListener = current;
            _passiveFailures = 1;
        }

        return true;
    }

    // Before a call is dispatched, whatever the reconciliation switch says, and in every round: a Hub that connected
    // while the startup lease was busy, or that could not register, tries again. Cheap when the presence is held (a
    // dictionary look). True when the backend went offline because the Gateway it was connected to is gone.
    public async Task<bool> EnsurePresenceAsync(CancellationToken cancellationToken)
    {
        var endpoint = Endpoint;
        if (!Config.Enabled || endpoint is null || !Online || Presence.IsHeld(endpoint) ||
            DateTime.UtcNow < _presenceRetryAfterUtc)
        {
            return false;
        }

        return await RetryPresenceAsync(endpoint, cancellationToken);
    }

    private async Task<bool> RetryPresenceAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!Online || ConnectedListener is not ListenerLookup connected)
            {
                return false;
            }

            GatewayLeftWhileConnecting = false;
            if (await EnterPresenceAsync(new[] { endpoint }, endpoint, connected, cancellationToken))
            {
                return false;
            }

            if (!GatewayLeftWhileConnecting)
            {
                // Another process listens now: the reconciliation connects to it.
                return false;
            }

            GoOffline();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // The connection this runtime had is not valid any more: nothing listens where it connected.
    private void GoOffline()
    {
        Online = false;
        Tools = Array.Empty<BackendTool>();
        ConnectedEndpoint = null;
        ConnectedListener = null;
        ServerInfo = null;
        _client?.Dispose();
        _client = null;
        Error = "o Gateway deixou de escutar enquanto este Hub se conectava";
        LeavePresence();
    }

    // Whether the process that listens on the endpoint is the process this Hub started for the backend, or one that
    // process started (a wrapper script that launches the Gateway). False only when the process of this Hub is
    // certainly not in the ancestry of the listener. Null when it cannot be told: nobody listens, the listener cannot
    // be inspected, the process list could not be read, or the ancestry cannot be trusted.
    private bool? ListenerIsOwn(ListenerLookup listener)
    {
        if (_supervisor.ProcessId is not int ownProcessId)
        {
            return false;
        }

        if (listener.State != ListenerLookupState.Found || listener.ProcessId is not int listenerProcessId)
        {
            return null;
        }

        return _isSameOrDescendant(ownProcessId, listenerProcessId);
    }

    // A process of this Hub that is alive while a process outside its tree certainly owns the port.
    private bool HasLostProcess(ListenerLookup listener) =>
        _supervisor.StartedByHub && ListenerIsOwn(listener) == false;

    private void StopLostProcess(Uri endpoint)
    {
        if (_supervisor.StartedByHub && HasLostProcess(_listenerLookup(endpoint)))
        {
            _supervisor.Stop("outro processo é o dono da porta do backend");
        }
    }

    public JsonObject StatusJson(string loadedRevision, string? onDiskRevision, DateTime? profileModifiedUtc)
    {
        return new JsonObject
        {
            ["id"] = Config.Id,
            ["displayName"] = Config.DisplayName,
            ["kind"] = Config.Kind,
            ["enabled"] = Config.Enabled,
            ["online"] = Online,
            ["endpoint"] = ConnectedEndpoint?.ToString(),
            ["tools"] = Tools.Count,
            ["startedByHub"] = _supervisor.StartedByHub,
            ["processId"] = _supervisor.ProcessId,
            ["pinsConfigured"] = PinsStatus(Config),
            ["gateway"] = GatewayStatus(loadedRevision, onDiskRevision, profileModifiedUtc),
            ["error"] = Error
        };
    }

    // The process behind the endpoint and whether it runs with the profile as it is now. A Gateway this Hub started
    // (the process that listens is the one this Hub started, or one started by it) is judged by the revision it was
    // started from; one started by another launcher (another Hub, the promotion, a client) is judged by time:
    // created after the last edit of the profile JSON or before it, which is evidence and not proof. No pin value,
    // no path.
    private JsonObject GatewayStatus(string loadedRevision, string? onDiskRevision, DateTime? profileModifiedUtc)
    {
        var endpoint = ResolveCandidates().FirstOrDefault();
        var listener = endpoint is null ? ListenerLookup.NotFound : _listenerLookup(endpoint);
        var startedByThisHub = ListenerIsOwn(listener) == true;
        var startedFrom = startedByThisHub ? _supervisor.StartedFromProfileRevision : null;
        var (applied, evidence) = GatewayProfileEvidence.Evaluate(
            listener,
            startedByThisHub,
            startedFrom,
            loadedRevision,
            onDiskRevision,
            profileModifiedUtc);

        return new JsonObject
        {
            ["listenerProcessId"] = listener.ProcessId,
            ["startedUtc"] = listener.StartedUtc?.ToString("O"),
            ["startedByThisHub"] = startedByThisHub,
            ["startedFromProfileRevision"] = startedFrom,
            // Whether the Gateway this Hub started is in a job object, which is where the system puts the children of
            // a client that runs its servers in one: such a Gateway can die when the client closes the job (or the
            // application), whatever this Hub decides. Null when it could not be told and for a Gateway this Hub did
            // not start. A diagnostic, not a decision.
            ["startedInJob"] = startedByThisHub && _supervisor.StartedInJob is bool inJob
                ? JsonValue.Create(inJob)
                : null,
            // How the process this Hub started was started: "hub-tree" (the usual way), "outside-job" (by the
            // detached launcher, in no job), "hub-tree-fallback" (the launcher failed). Null for a Gateway this Hub
            // did not start. startedInJob above is what the system says of the process, whichever way it started.
            ["startMode"] = startedByThisHub && _supervisor.StartMode is string mode
                ? JsonValue.Create(mode)
                : null,
            // How many other Hubs are connected to this endpoint now (null when it cannot be told). Worth a look
            // before gxhub_refresh, which restarts a Gateway of this Hub whoever else is using it.
            ["otherHubsConnected"] = OtherHubsConnected(endpoint) is int others ? JsonValue.Create(others) : null,
            ["profileModifiedUtc"] = profileModifiedUtc?.ToString("O"),
            ["profileApplied"] = applied is bool value ? JsonValue.Create(value) : null,
            ["profileAppliedEvidence"] = evidence
        };
    }

    private int? OtherHubsConnected(Uri? endpoint) => endpoint is null ? null : Presence.CountOthers(endpoint);

    // What the profile this Hub loaded declares for the write-destination pins, as booleans only: never the
    // values. It is intent, not the running Gateway: the Worker's effectivePins say what was applied, and
    // gateway.profileApplied above says whether the Gateway behind the endpoint runs with this profile.
    internal static JsonObject PinsStatus(BackendConfig config)
    {
        // The environment the Gateway will be started with (names compared without case, last one wins), not the
        // raw dictionary: a pin written in another case is still the pin the Worker receives.
        var environment = config.Startup is null
            ? null
            : ProcessSupervisor.BuildStartupEnvironment(config.Startup, null);
        return new JsonObject
        {
            ["kbPathConfigured"] = HasValue(environment, "GXMCP_EXPECTED_KB_PATH"),
            ["versionConfigured"] = HasValue(environment, "GXMCP_EXPECTED_KB_VERSION")
        };
    }

    private static bool HasValue(IReadOnlyDictionary<string, string>? environment, string name) =>
        environment is not null &&
        environment.TryGetValue(name, out var value) &&
        !string.IsNullOrWhiteSpace(value);

    private BackendTool ToBackendTool(JsonObject definition)
    {
        var name = definition["name"]?.GetValue<string>()
                   ?? throw new BackendRpcException(-32603, "Tool sem nome em tools/list.");
        var annotationReadOnly = definition["annotations"]?["readOnlyHint"]?.GetValue<bool>() == true;
        var allowListed = Config.ReadOnlyTools.Contains(name, StringComparer.OrdinalIgnoreCase);
        return new BackendTool(Config.Id, name, Config.ToolPrefix + name, definition, annotationReadOnly, allowListed);
    }

    private IEnumerable<Uri> ResolveCandidates()
    {
        if (!string.IsNullOrWhiteSpace(Config.Endpoint) &&
            Uri.TryCreate(Config.Endpoint, UriKind.Absolute, out var fixedEndpoint))
        {
            yield return fixedEndpoint;
        }

    }

    private async Task<bool> AnyEndpointListeningAsync(Uri[] candidates, CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            if (await IsListeningAsync(candidate, TimeSpan.FromMilliseconds(500), cancellationToken))
            {
                return true;
            }
        }
        return false;
    }

    private async Task WaitForEndpointAsync(Uri[] candidates, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Config.Startup?.StartupTimeoutSeconds ?? 30);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await AnyEndpointListeningAsync(candidates, cancellationToken))
            {
                return;
            }
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException($"Backend '{Config.Id}' não abriu o endpoint em {timeout.TotalSeconds}s.");
    }

    private static async Task<bool> IsListeningAsync(
        Uri endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await client.ConnectAsync(endpoint.Host, endpoint.Port, timeoutCts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateLoopback(Uri endpoint)
    {
        if (!endpoint.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfigurationException($"Somente HTTP local é aceito: {endpoint}");
        }

        if (endpoint.IsLoopback || endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address))
        {
            return;
        }

        throw new ConfigurationException($"Endpoint não-loopback recusado: {endpoint}");
    }

    // The backend is discarded by the Hub that keeps running (a profile reload): a Gateway this Hub started is
    // stopped, as always, whoever else is using it. gxhub_refresh is the deliberate act that does it. The presence
    // of the Hub is not touched here: the backend that takes the place of this one may use the same endpoint.
    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        await _supervisor.DisposeAsync();
        if (_ownsPresence)
        {
            _presence?.Dispose();
        }

        _gate.Dispose();
    }

    // What a Hub decided about the Gateway it started, on its way out.
    internal const string DecisionStopped = "stopped";
    internal const string DecisionKept = "kept";
    internal const string DecisionNotOwner = "not-owner";
    internal const string DecisionLeftForLease = "left-for-lease";
    internal const string DecisionError = "error";

    // The normal shutdown of the Hub. A Gateway this Hub started may be serving other Hubs at this very moment, so
    // it is only stopped when no other Hub is connected to the endpoint. The question is asked under the startup
    // lease of the endpoint, where the other Hubs register their presence: this Hub lets go of its own presence,
    // looks at the others and, with none held, stops the Gateway before releasing the lease. With another one held,
    // or without an answer (the lease was not obtained in time, a presence could not be examined), the Gateway is
    // left running and only the process handle is released: one Gateway too many is the safe mistake.
    // keepSharedGateway false is the previous behaviour: the Gateway of this Hub is always stopped.
    // keepGatewayWhenAlone (GXMCP_HUB_KEEP_GATEWAY_WHEN_ALONE) leaves the Gateway running even when no other Hub is
    // connected, so that the next Hub finds the Worker warm. The question is asked all the same, so the record is a kept
    // Gateway with 0 other Hubs; every case that stops it without asking (keepSharedGateway false, no listener, a lost
    // process) still stops it.
    public async ValueTask<ShutdownDecision> ShutdownAsync(
        bool keepSharedGateway, TimeSpan leaseWait, bool keepGatewayWhenAlone = false)
    {
        _client?.Dispose();
        _client = null;
        var decision = new ShutdownDecision(DecisionNotOwner, null, null);
        try
        {
            decision = await StopOrReleaseOwnProcessAsync(keepSharedGateway, leaseWait, keepGatewayWhenAlone);
        }
        catch (Exception ex) when (keepSharedGateway)
        {
            // The question could not be asked: without an answer the Gateway is left running.
            var processId = _supervisor.ProcessId;
            var startMode = _supervisor.StartMode;
            _supervisor.Detach($"a consulta aos outros Hubs falhou ({ex.GetType().Name})");
            decision = new ShutdownDecision(DecisionError, processId, null, startMode);
        }
        finally
        {
            LeavePresence();
            if (_ownsPresence)
            {
                _presence?.Dispose();
            }

            // Nothing is left to stop by now: this only releases what the supervisor still holds.
            await _supervisor.DisposeAsync();
            _gate.Dispose();
        }

        return decision;
    }

    private async Task<ShutdownDecision> StopOrReleaseOwnProcessAsync(
        bool keepSharedGateway, TimeSpan leaseWait, bool keepGatewayWhenAlone)
    {
        // A Gateway this Hub did not start is never stopped by it.
        if (!_supervisor.StartedByHub)
        {
            return new ShutdownDecision(DecisionNotOwner, null, null);
        }

        // Read before anything stops or releases the process: afterwards the supervisor has no mode to tell.
        var processId = _supervisor.ProcessId;
        var startMode = _supervisor.StartMode;
        var candidates = ResolveCandidates().ToArray();
        if (!keepSharedGateway || candidates.Length == 0)
        {
            _supervisor.Stop();
            return new ShutdownDecision(DecisionStopped, processId, null, startMode);
        }

        // A process of this Hub that is nobody's Gateway (nobody listens on the endpoint, or a process outside its
        // tree owns the port) serves no one and is stopped as before.
        var endpoint = candidates[0];
        var listener = _listenerLookup(endpoint);
        if (listener.State == ListenerLookupState.NotFound || HasLostProcess(listener))
        {
            _supervisor.Stop();
            return new ShutdownDecision(DecisionStopped, processId, null, startMode);
        }

        using var lease = await BackendStartupLease.TryAcquireAsync(candidates, leaseWait, CancellationToken.None);
        if (lease is null)
        {
            _supervisor.Detach(
                "a trava de início do endpoint não foi obtida a tempo de saber se há outro Hub conectado");
            return new ShutdownDecision(DecisionLeftForLease, processId, null, startMode);
        }

        Presence.Leave(endpoint);
        var others = Presence.CountOthers(endpoint);
        if (others == 0 && keepGatewayWhenAlone && CanLeaveRunningWhenAlone(endpoint))
        {
            // Nobody else is connected and this Hub was asked to leave the Gateway anyway: it keeps serving, and the
            // next Hub finds it (and a warm Worker) instead of starting another. Released under the lease like the
            // other kept cases, so a Hub that is arriving registers its presence on a Gateway that is still there.
            _supervisor.Detach(
                "nenhum outro Hub conectado ao endpoint, e GXMCP_HUB_KEEP_GATEWAY_WHEN_ALONE o mantém");
            return new ShutdownDecision(DecisionKept, processId, 0, startMode);
        }

        if (others == 0)
        {
            // Stopped before the lease is released: a Hub that is arriving only registers its presence after this,
            // and then finds the Gateway gone instead of losing it later.
            _supervisor.Stop();
            await WaitUntilNobodyListensAsync(endpoint);
            return new ShutdownDecision(DecisionStopped, processId, null, startMode);
        }

        _supervisor.Detach(others is int count
            ? $"{count} outro(s) Hub(s) conectado(s) ao endpoint"
            : "não foi possível saber se há outro Hub conectado ao endpoint");
        return new ShutdownDecision(DecisionKept, processId, others, startMode);
    }

    // A Gateway is only left running for the next Hub when this Hub saw it work (it connected and was not cut off since)
    // and the process that listens on the endpoint, looked up now under the lease, is certainly the one this Hub started
    // or one it started in turn (a wrapper script). A Gateway that never answered the handshake, or one that cannot be
    // told apart from somebody else's process (not inspectable, the process list unreadable), is stopped as in 0.4.0:
    // leaving it would leave the next Hub a port held by something that may serve nobody.
    private bool CanLeaveRunningWhenAlone(Uri endpoint) =>
        Online && ListenerIsOwn(_listenerLookup(endpoint)) == true;

    // Stop returns when the process this Hub started is gone, but what listens may be a process that one started in
    // turn (a wrapper script that launches the Gateway), still closing. The lease is only released when nothing
    // listens any more, or after a short wait, so that a Hub that obtains it next finds the Gateway gone.
    private async Task WaitUntilNobodyListensAsync(Uri endpoint)
    {
        var deadline = DateTime.UtcNow + ListenerGoneWait;
        while (_listenerLookup(endpoint).State == ListenerLookupState.Found && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }
}

// What a Hub decided about the Gateway it started when it left, for the exit record: the decision, the process id of
// the process it started, when the Gateway was kept how many other Hubs were connected (null: could not be told) and
// how that process had been started (StartModes; null when this Hub started nothing).
internal readonly record struct ShutdownDecision(
    string Decision,
    int? GatewayProcessId,
    int? OthersConnected,
    string? StartMode = null);

internal sealed class BackendStartupLease : IDisposable
{
    private readonly FileStream _stream;

    private BackendStartupLease(FileStream stream)
    {
        _stream = stream;
    }

    // What names an endpoint in the folders the Hubs share: the startup lease file and the presence folder.
    internal static string HashOf(IEnumerable<Uri> endpoints) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(IdentityOf(endpoints))));

    private static string IdentityOf(IEnumerable<Uri> endpoints) => string.Join(
        "|",
        endpoints.Select(endpoint => endpoint.AbsoluteUri).OrderBy(value => value, StringComparer.Ordinal));

    // Null when the lease was not obtained within the time given, whatever the reason.
    public static async Task<BackendStartupLease?> TryAcquireAsync(
        IEnumerable<Uri> endpoints,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await AcquireAsync(endpoints, timeout, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static async Task<BackendStartupLease> AcquireAsync(
        IEnumerable<Uri> endpoints,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var identity = IdentityOf(endpoints);
        var hash = HashOf(endpoints);
        var lockRoot = Path.Combine(Path.GetTempPath(), "GxMcpHub", "backend-startup");
        Directory.CreateDirectory(lockRoot);
        var lockPath = Path.Combine(lockRoot, hash + ".lock");
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return new BackendStartupLease(stream);
            }
            catch (IOException)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        throw new TimeoutException(
            $"Timeout aguardando coordenação de inicialização do backend em {identity}.");
    }

    public void Dispose() => _stream.Dispose();
}

internal sealed record BackendTool(
    string BackendId,
    string OriginalName,
    string ExposedName,
    JsonObject Definition,
    bool AnnotationReadOnly,
    bool AllowListedReadOnly)
{
    public bool IsReadOnly(BackendConfig config) =>
        AllowListedReadOnly || (config.UseRemoteReadOnlyAnnotations && AnnotationReadOnly);
}
